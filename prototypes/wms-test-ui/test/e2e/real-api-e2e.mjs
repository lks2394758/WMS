// Real end-to-end check: the built WMS API (dotnet, SQLite) behind this UI server's /api proxy.
//
//   dotnet build src/Wms.Api            (once, from the repository root)
//   node prototypes/wms-test-ui/test/e2e/real-api-e2e.mjs
//
// It never touches src/Wms.Api/App_Data: the API runs against a new SQLite file in a temporary folder, with a random
// seed password and JWT signing key that exist only in this process's memory and the child's environment. Nothing
// secret is printed. The temporary folder is deleted at the end.

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes, randomUUID } from "node:crypto";
import { once } from "node:events";
import { existsSync, mkdtempSync, readFileSync, rmSync, createWriteStream } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { createAppServer, loadConfig } from "../../server.mjs";

const repo = fileURLToPath(new URL("../../../../", import.meta.url));
const apiDir = join(repo, "src", "Wms.Api");
const apiDll = join(apiDir, "bin", "Debug", "net10.0", "Wms.Api.dll");
if (!existsSync(apiDll)) throw new Error("Build the API first: dotnet build src/Wms.Api");

const work = mkdtempSync(join(tmpdir(), "wms-ui-e2e-"));
const secrets = { password: randomBytes(18).toString("base64url"), signingKey: randomBytes(32).toString("base64") };
const issuedTokens = [];
const results = [];
const proxyLog = [];

async function freePort() {
  const server = createServer().listen(0, "127.0.0.1");
  await once(server, "listening");
  const { port } = server.address();
  server.close();
  return port;
}

const apiPort = await freePort();
const ui = createAppServer({ ...loadConfig({ WMS_API_BASE_URL: `http://127.0.0.1:${apiPort}` }), timeoutMs: 15000 }, { logger: { log: line => proxyLog.push(line) } });
ui.listen(0, "127.0.0.1");
await once(ui, "listening");
const base = `http://127.0.0.1:${ui.address().port}`;

let api;
let apiRun = 0;
async function startApi(extraEnv = {}) {
  apiRun += 1;
  const log = createWriteStream(join(work, `api-${apiRun}.log`));
  api = spawn("dotnet", [apiDll], {
    cwd: apiDir,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: `http://127.0.0.1:${apiPort}`,
      Persistence__Provider: "Sqlite",
      Persistence__Sqlite__DataSource: join(work, "wms-e2e.db"),
      DevelopmentSeed__Password: secrets.password,
      Jwt__SigningKey: secrets.signingKey,
      ...extraEnv
    },
    stdio: ["ignore", "pipe", "pipe"]
  });
  api.stdout.pipe(log);
  api.stderr.pipe(log);
  const deadline = Date.now() + 90000;
  while (Date.now() < deadline) {
    const response = await fetch(`${base}/health/ready`).catch(() => null);
    if (response?.status === 200) return;
    if (api.exitCode !== null || api.signalCode !== null) throw new Error(`API exited with ${api.exitCode}; see the log in ${work}`);
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error("API did not become ready in 90 s");
}

async function stopApi() {
  if (!api || api.exitCode !== null || api.signalCode !== null) return; // a killed child has signalCode, not exitCode
  const exited = once(api, "exit");
  api.kill();
  await exited;
}

async function call(method, path, { token, body, key, raw } = {}) {
  const headers = { Accept: "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (key) headers["Idempotency-Key"] = key;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const started = Date.now();
  const response = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : raw ? body : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
  const text = await response.text();
  return { status: response.status, headers: response.headers, data: text ? JSON.parse(text) : null, ms: Date.now() - started };
}

function check(name, fn) {
  return Promise.resolve().then(fn).then(
    () => results.push(`PASS ${name}`),
    error => { results.push(`FAIL ${name}: ${error.message}`); throw error; });
}

async function signIn(username, password = secrets.password) {
  const response = await call("POST", "/api/v1/auth/login", { body: { username, password } });
  assert.equal(response.status, 200, `login ${username}`);
  issuedTokens.push(response.data.accessToken, response.data.refreshToken);
  return response.data;
}

const ids = {};
const balanceOf = async (token, productId, locationId) => {
  const { data } = await call("GET", `/api/v1/inventory/balances?productId=${productId}&locationId=${locationId}`, { token });
  return data.items[0]?.availableQuantity ?? 0;
};

try {
  await startApi();
  const s = {};
  const receiptKey = randomUUID();
  let receiptBody;
  let receiptNo;

  await check("static UI is served through the same origin", async () => {
    const page = await fetch(`${base}/`);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /仓储测试工作台/);
  });

  await check("wrong password -> 401 UNAUTHENTICATED", async () => {
    const response = await call("POST", "/api/v1/auth/login", { body: { username: "receiver", password: "wrong-password" } });
    assert.equal(response.status, 401);
    assert.equal(response.data.code, "UNAUTHENTICATED");
  });

  await check("login + /auth/me for all four seeded roles", async () => {
    for (const [username, role] of [["admin", "systemAdministrator"], ["supervisor", "warehouseSupervisor"], ["receiver", "receivingClerk"], ["shipper", "shippingClerk"]]) {
      s[username] = await signIn(username);
      const me = await call("GET", "/api/v1/auth/me", { token: s[username].accessToken });
      assert.equal(me.status, 200);
      assert.equal(me.data.role, role);
    }
  });

  await check("refresh rotates; reusing the old refresh token -> 401", async () => {
    const session = await signIn("admin");
    const refreshed = await call("POST", "/api/v1/auth/refresh", { body: { refreshToken: session.refreshToken } });
    assert.equal(refreshed.status, 200);
    issuedTokens.push(refreshed.data.accessToken, refreshed.data.refreshToken);
    assert.notEqual(refreshed.data.refreshToken, session.refreshToken);
    const reused = await call("POST", "/api/v1/auth/refresh", { body: { refreshToken: session.refreshToken } });
    assert.equal(reused.status, 401);
  });

  await check("no token -> 401", async () => {
    assert.equal((await call("GET", "/api/v1/products")).status, 401);
  });

  await check("catalog reads: products, locations, by id, barcode resolve, empty balances", async () => {
    const token = s.receiver.accessToken;
    const products = await call("GET", "/api/v1/products?limit=200", { token });
    const locations = await call("GET", "/api/v1/locations?limit=200", { token });
    assert.equal(products.status, 200);
    assert.equal(locations.status, 200);
    ids.widget = products.data.items.find(p => p.sku === "WIDGET-001").id;
    ids.a0101 = locations.data.items.find(l => l.code === "A-01-01").id;
    ids.a0102 = locations.data.items.find(l => l.code === "A-01-02").id;
    assert.equal((await call("GET", `/api/v1/products/${ids.widget}`, { token })).data.sku, "WIDGET-001");
    assert.equal((await call("GET", `/api/v1/locations/${ids.a0101}`, { token })).data.code, "A-01-01");
    const resolved = await call("GET", "/api/v1/barcodes/resolve?value=4006381333931", { token });
    assert.equal(resolved.data.product.id, ids.widget);
    assert.equal((await call("GET", "/api/v1/barcodes/resolve?value=0000000000000", { token })).status, 404);
    const balances = await call("GET", "/api/v1/inventory/balances", { token });
    assert.deepEqual(balances.data.items, []); // fresh database: no seeded stock
  });

  await check("receipt by receiver commits, replay returns Idempotent-Replayed, changed body -> 409", async () => {
    receiptBody = { locationId: ids.a0101, lines: [{ productId: ids.widget, quantity: 10 }], referenceNo: "PO-E2E-1" };
    const first = await call("POST", "/api/v1/inventory/receipts", { token: s.receiver.accessToken, key: receiptKey, body: receiptBody });
    assert.equal(first.status, 200);
    assert.match(first.data.transactionNo, /^RCV-\d{8}-\d{6}$/);
    assert.equal(first.headers.get("idempotent-replayed"), null);
    receiptNo = first.data.transactionNo;
    const replay = await call("POST", "/api/v1/inventory/receipts", { token: s.receiver.accessToken, key: receiptKey, body: receiptBody });
    assert.equal(replay.status, 200);
    assert.equal(replay.headers.get("idempotent-replayed"), "true");
    assert.equal(replay.data.transactionNo, receiptNo);
    const changed = await call("POST", "/api/v1/inventory/receipts", { token: s.receiver.accessToken, key: receiptKey, body: { ...receiptBody, referenceNo: "PO-E2E-2" } });
    assert.equal(changed.status, 409);
    assert.equal(changed.data.code, "IDEMPOTENCY_CONFLICT");
    assert.equal(await balanceOf(s.receiver.accessToken, ids.widget, ids.a0101), 10);
  });

  await check("400 field errors: zero quantity, missing Idempotency-Key", async () => {
    const zero = await call("POST", "/api/v1/inventory/receipts", { token: s.receiver.accessToken, key: randomUUID(), body: { ...receiptBody, lines: [{ productId: ids.widget, quantity: 0 }] } });
    assert.equal(zero.status, 400);
    assert.ok(zero.data.errors["lines[0].quantity"]);
    const noKey = await call("POST", "/api/v1/inventory/receipts", { token: s.receiver.accessToken, body: receiptBody });
    assert.equal(noKey.status, 400);
    assert.ok(noKey.data.errors["Idempotency-Key"]);
  });

  await check("403: receiver cannot ship or adjust", async () => {
    const ship = await call("POST", "/api/v1/inventory/shipments", { token: s.receiver.accessToken, key: randomUUID(), body: { locationId: ids.a0101, lines: [{ productId: ids.widget, quantity: 1 }] } });
    assert.equal(ship.status, 403);
    assert.equal(ship.data.code, "FORBIDDEN");
    const adjust = await call("POST", "/api/v1/inventory/adjustments", { token: s.receiver.accessToken, key: randomUUID(), body: { locationId: ids.a0101, reason: "x", lines: [{ productId: ids.widget, quantityDelta: 1 }] } });
    assert.equal(adjust.status, 403);
  });

  await check("shipment by shipper commits; too much -> 409 INSUFFICIENT_STOCK", async () => {
    const ship = await call("POST", "/api/v1/inventory/shipments", { token: s.shipper.accessToken, key: randomUUID(), body: { locationId: ids.a0101, lines: [{ productId: ids.widget, quantity: 3 }], referenceNo: "SO-E2E-1" } });
    assert.equal(ship.status, 200);
    assert.equal(ship.data.lines[0].from.after, 7);
    const tooMuch = await call("POST", "/api/v1/inventory/shipments", { token: s.shipper.accessToken, key: randomUUID(), body: { locationId: ids.a0101, lines: [{ productId: ids.widget, quantity: 100 }] } });
    assert.equal(tooMuch.status, 409);
    assert.equal(tooMuch.data.code, "INSUFFICIENT_STOCK");
    assert.equal(await balanceOf(s.shipper.accessToken, ids.widget, ids.a0101), 7);
  });

  await check("movement by receiver, count and adjustment by supervisor", async () => {
    const move = await call("POST", "/api/v1/inventory/movements", { token: s.receiver.accessToken, key: randomUUID(), body: { fromLocationId: ids.a0101, toLocationId: ids.a0102, lines: [{ productId: ids.widget, quantity: 2 }] } });
    assert.equal(move.status, 200);
    assert.match(move.data.transactionNo, /^MOV-/);
    const count = await call("POST", "/api/v1/inventory/counts", { token: s.supervisor.accessToken, key: randomUUID(), body: { locationId: ids.a0102, reason: "E2E cycle count", lines: [{ productId: ids.widget, countedQuantity: 4 }] } });
    assert.equal(count.status, 200);
    assert.match(count.data.transactionNo, /^CNT-/);
    const adjust = await call("POST", "/api/v1/inventory/adjustments", { token: s.supervisor.accessToken, key: randomUUID(), body: { locationId: ids.a0101, reason: "E2E damaged unit", lines: [{ productId: ids.widget, quantityDelta: -1 }] } });
    assert.equal(adjust.status, 200);
    assert.match(adjust.data.transactionNo, /^ADJ-/);
    assert.equal(await balanceOf(s.supervisor.accessToken, ids.widget, ids.a0101), 4);
    assert.equal(await balanceOf(s.supervisor.accessToken, ids.widget, ids.a0102), 4);
  });

  await check("API stopped: proxy answers 502 API_UNAVAILABLE quickly, never hangs", async () => {
    await stopApi();
    const health = await call("GET", "/health/ready");
    assert.equal(health.status, 502);
    const login = await call("POST", "/api/v1/auth/login", { body: { username: "receiver", password: "x" } });
    assert.equal(login.status, 502);
    assert.equal(login.data.code, "API_UNAVAILABLE");
    assert.ok(login.ms < 5000, `${login.ms} ms`);
  });

  await check("restart on the same file: balances, refresh token and idempotent replay survive", async () => {
    await startApi();
    const refreshed = await call("POST", "/api/v1/auth/refresh", { body: { refreshToken: s.receiver.refreshToken } });
    assert.equal(refreshed.status, 200);
    issuedTokens.push(refreshed.data.accessToken, refreshed.data.refreshToken);
    const token = refreshed.data.accessToken;
    assert.equal(await balanceOf(token, ids.widget, ids.a0101), 4);
    assert.equal(await balanceOf(token, ids.widget, ids.a0102), 4);
    const replay = await call("POST", "/api/v1/inventory/receipts", { token, key: receiptKey, body: receiptBody });
    assert.equal(replay.status, 200);
    assert.equal(replay.headers.get("idempotent-replayed"), "true");
    assert.equal(replay.data.transactionNo, receiptNo);
    assert.equal(await balanceOf(token, ids.widget, ids.a0101), 4);
    await stopApi();
  });

  await check("429 RATE_LIMITED with Retry-After passes through the proxy", async () => {
    await startApi({ RateLimiting__PermitLimit: "3", RateLimiting__WindowSeconds: "60" });
    const session = await signIn("admin");
    let last;
    for (let i = 0; i < 5; i += 1) last = await call("GET", "/api/v1/auth/me", { token: session.accessToken });
    assert.equal(last.status, 429);
    assert.equal(last.data.code, "RATE_LIMITED");
    assert.ok(Number(last.headers.get("retry-after")) > 0);
    await stopApi();
  });

  await check("no password, signing key or token in the API or proxy logs", async () => {
    const logs = [...Array.from({ length: apiRun }, (_, i) => readFileSync(join(work, `api-${i + 1}.log`), "utf8")), proxyLog.join("\n")].join("\n");
    for (const secret of [secrets.password, secrets.signingKey, ...issuedTokens]) assert.ok(!logs.includes(secret), "a secret value appears in a log");
    assert.ok(proxyLog.length > 0);
  });
} finally {
  await stopApi();
  ui.close();
  console.log(results.join("\n"));
  console.log(`${results.filter(r => r.startsWith("PASS")).length} passed, ${results.filter(r => r.startsWith("FAIL")).length} failed`);
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
}
