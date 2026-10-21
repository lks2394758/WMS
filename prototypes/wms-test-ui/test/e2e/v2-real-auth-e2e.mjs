// Real v2 authentication behind the UI proxy. NOT inventory/count workflow acceptance.
// Build first: dotnet build src/Wms.Api (repository root).
// Run here: node test/e2e/v2-real-auth-e2e.mjs
// Only new, validated temporary files are used. Credentials stay in memory/child environment.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { existsSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { request as httpRequest } from "node:http";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createAppServer, loadConfig } from "../../server.mjs";

const repo = fileURLToPath(new URL("../../../../", import.meta.url));
const apiDir = join(repo, "src", "Wms.Api");
const dll = join(apiDir, "bin", "Debug", "net10.0", "Wms.Api.dll");
if (!existsSync(dll)) throw new Error("Build src/Wms.Api before running this test.");
const work = mkdtempSync(join(tmpdir(), "wms-v2-real-auth-"));
const password = randomBytes(24).toString("base64url");
const legacyPath = join(work, "wms-demo.db"), modernPath = join(work, "wms-v2-demo.db");
const sentinel = randomBytes(4096); writeFileSync(legacyPath, sentinel);
const logs = [], credentials = [], results = [];
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function freePort() {
  const probe = createServer().listen(0, "127.0.0.1"); await once(probe, "listening");
  const port = probe.address().port; await new Promise(resolve => probe.close(resolve)); return port;
}
const port = await freePort(), upstream = `http://127.0.0.1:${port}`;
let child, ui, base;
async function stopApi() {
  if (!child?.pid || child.exitCode !== null || child.signalCode !== null) return;
  const ended = once(child, "exit"); child.kill(); await ended;
}
async function startApi() {
  child = spawn("dotnet", [dll], { cwd: apiDir, windowsHide: true, stdio: ["ignore", "pipe", "pipe"], env: {
    ...process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: upstream,
    V2__Enabled: "true", V2__Sqlite__DataSource: modernPath, V2__Seed__Enabled: "true",
    V2__Seed__Password: password, Persistence__Sqlite__DataSource: legacyPath,
    Logging__LogLevel__Default: "Warning"
  } });
  child.stdout.on("data", data => logs.push(data.toString())); child.stderr.on("data", data => logs.push(data.toString()));
  let launchError; child.on("error", error => { launchError = error; });
  for (let i = 0; i < 120; i++) {
    if (launchError) throw new Error("Could not launch the built API.");
    if (child.exitCode !== null || child.signalCode !== null) throw new Error("The v2 API exited before readiness; run the focused backend tests for diagnostics.");
    const ready = await fetch(upstream + "/health/ready", { signal: AbortSignal.timeout(1000) }).catch(() => null);
    if (ready?.status === 200) return;
    await pause(250);
  }
  throw new Error("The v2 API was not ready within 30 seconds.");
}
async function request(path, { method = "GET", body, cookie } = {}) {
  return fetch(base + "/api/v2/" + path, { method, headers: {
    Accept: "application/json", ...(cookie ? { Cookie: cookie } : {}),
    ...(body === undefined ? {} : { "Content-Type": "application/json", Origin: base })
  }, body: body === undefined ? undefined : JSON.stringify(body) });
}
async function login(username) {
  const response = await request("auth/login", { method: "POST", body: { username, password } });
  assert.equal(response.status, 200);
  const body = await response.json(), cookie = response.headers.get("set-cookie");
  assert.ok(cookie?.includes("HttpOnly") && cookie.includes("SameSite=Strict"));
  assert.ok(!body.token && !body.accessToken && !body.refreshToken);
  const pair = cookie.split(";", 1)[0]; credentials.push(pair.split("=")[1]);
  return { cookie: pair, body };
}
async function check(name, action) { await action(); results.push(name); console.log("PASS " + name); }
async function chunkedOversizedRequest() {
  return new Promise((resolve, reject) => {
    const request = httpRequest(upstream + "/api/v2/auth/login", { method: "POST", headers: { "Content-Type": "application/json", "Transfer-Encoding": "chunked" } }, response => {
      const parts = []; response.on("data", part => parts.push(part)); response.on("end", () => {
        try { resolve({ status: response.statusCode, body: JSON.parse(Buffer.concat(parts).toString()) }); }
        catch { reject(new Error("The size-limit probe received a non-JSON response.")); }
      });
      response.on("error", reject);
    });
    request.on("error", reject); request.setTimeout(10000, () => request.destroy(new Error("Oversize limit probe timed out")));
    request.write('{"username":"limit-test","password":"'); request.write("x".repeat(5 * 1024 * 1024)); request.end('"}');
  });
}
try {
  await startApi();
  ui = createAppServer(loadConfig({ WMS_V2_API_BASE_URL: upstream }), { logger: { log: line => logs.push(line) } });
  ui.listen(0, "127.0.0.1"); await once(ui, "listening"); base = `http://127.0.0.1:${ui.address().port}`;
  let account;
  await check("real v2 login, cookie protection and persisted current-role permissions", async () => {
    account = await login("receiver");
    assert.equal(account.body.user.currentRole, "receivingClerk");
    assert.deepEqual(account.body.user.permissions, ["Inventory.Receive"]);
    const me = await request("auth/me", { cookie: account.cookie }); assert.equal(me.status, 200);
    assert.equal((await me.json()).user.id, account.body.user.id);
    const denied = await request("auth/switch-role", { method: "POST", body: { role: "systemAdministrator" }, cookie: account.cookie });
    assert.equal(denied.status, 403); assert.equal((await denied.json()).code, "ROLE_NOT_ASSIGNED");
  });
  await check("same-role switch preserves the existing cookie and expiry", async () => {
    const response = await request("auth/switch-role", { method: "POST", body: { role: "receivingClerk" }, cookie: account.cookie });
    assert.equal(response.status, 200); assert.equal(response.headers.get("set-cookie"), null);
    assert.equal((await response.json()).expiresAt, account.body.expiresAt);
  });
  await check("real SQLite session survives restarting the API", async () => {
    await stopApi(); await startApi();
    assert.equal((await request("auth/me", { cookie: account.cookie })).status, 200);
  });
  await check("a new login revokes the older cookie immediately", async () => {
    const newer = await login("receiver");
    const old = await request("auth/me", { cookie: account.cookie }); assert.equal(old.status, 401);
    assert.equal((await old.json()).code, "SESSION_REVOKED"); assert.ok(old.headers.get("set-cookie")?.includes("Max-Age=0"));
    account = newer;
  });
  await check("real logout returns 204 and revokes the stored session", async () => {
    const response = await request("auth/logout", { method: "POST", body: {}, cookie: account.cookie });
    assert.equal(response.status, 204); assert.ok(response.headers.get("set-cookie")?.includes("Max-Age=0"));
    assert.equal((await request("auth/me", { cookie: account.cookie })).status, 401);
  });
  await check("real HTTP server rejects declared and chunked oversized JSON bodies", async () => {
    const declared = await fetch(upstream + "/api/v2/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: "limit-test", password: "x".repeat(5 * 1024 * 1024) }) });
    assert.equal(declared.status, 413); assert.equal((await declared.json()).code, "PAYLOAD_TOO_LARGE");
    const chunked = await chunkedOversizedRequest(); assert.equal(chunked.status, 413); assert.equal(chunked.body.code, "PAYLOAD_TOO_LARGE");
  });
  await check("v2 process does not serve v1 or modify the legacy sentinel file", async () => {
    assert.equal((await fetch(upstream + "/api/v1/products")).status, 404);
    assert.equal((await fetch(upstream + "/openapi/v1.json")).status, 404);
    assert.ok(existsSync(modernPath)); assert.ok(readFileSync(legacyPath).equals(sentinel));
    assert.ok(!existsSync(legacyPath + "-wal") && !existsSync(legacyPath + "-shm"));
    const output = logs.join("\n"); assert.ok(!output.includes(password));
    for (const credential of credentials) assert.ok(!output.includes(credential));
  });
  console.log(`PASS ${results.length} REAL v2 auth/proxy checks; catalog/inventory/count acceptance is still pending`);
} finally {
  ui?.closeAllConnections(); if (ui) await new Promise(resolve => ui.close(resolve));
  await stopApi();
  const within = relative(resolve(tmpdir()), resolve(work));
  if (within && !within.startsWith("..") && !within.includes(":") && within.startsWith("wms-v2-real-auth-")) {
    try { rmSync(work, { recursive: true, force: true }); } catch { /* A stopped process may still be releasing its temporary SQLite handles. */ }
  }
}
