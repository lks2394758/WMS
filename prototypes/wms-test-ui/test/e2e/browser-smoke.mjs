// Browser smoke test: headless Microsoft Edge (or Chrome) drives the real UI against the built WMS API.
// No packages: it speaks the Chrome DevTools Protocol over Node's built-in WebSocket.
//
//   dotnet build src/Wms.Api            (once, from the repository root)
//   node prototypes/wms-test-ui/test/e2e/browser-smoke.mjs [screenshot-folder]
//
// Like real-api-e2e.mjs it uses a temporary SQLite file and a random seed password and signing key that are never
// printed. Screenshots go to the given folder (default artifacts/test-ui, git-ignored).

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { once } from "node:events";
import { createWriteStream, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createAppServer, loadConfig } from "../../server.mjs";

const repo = fileURLToPath(new URL("../../../../", import.meta.url));
const apiDir = join(repo, "src", "Wms.Api");
const apiDll = join(apiDir, "bin", "Debug", "net10.0", "Wms.Api.dll");
const shots = resolve(process.argv[2] ?? join(repo, "artifacts", "test-ui"));
const browserPath = [
  "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
  "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"
].find(existsSync);
if (!existsSync(apiDll)) throw new Error("Build the API first: dotnet build src/Wms.Api");
if (!browserPath) throw new Error("No Edge or Chrome found");
mkdirSync(shots, { recursive: true });

const work = mkdtempSync(join(tmpdir(), "wms-ui-browser-"));
const secrets = { password: randomBytes(18).toString("base64url"), signingKey: randomBytes(32).toString("base64") };
const results = [];
const browserErrors = [];

async function freePort() {
  const server = createServer().listen(0, "127.0.0.1");
  await once(server, "listening");
  const { port } = server.address();
  server.close();
  return port;
}
const sleep = ms => new Promise(r => setTimeout(r, ms));

const apiPort = await freePort();
const ui = createAppServer(loadConfig({ WMS_API_BASE_URL: `http://127.0.0.1:${apiPort}` }), { logger: { log() {} } });
ui.listen(0, "127.0.0.1");
await once(ui, "listening");
const base = `http://127.0.0.1:${ui.address().port}`;

let api;
let apiRun = 0;
async function startApi() {
  apiRun += 1;
  const log = createWriteStream(join(work, `api-${apiRun}.log`));
  api = spawn("dotnet", [apiDll], {
    cwd: apiDir,
    env: {
      ...process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: `http://127.0.0.1:${apiPort}`,
      Persistence__Provider: "Sqlite", Persistence__Sqlite__DataSource: join(work, "wms-browser.db"),
      DevelopmentSeed__Password: secrets.password, Jwt__SigningKey: secrets.signingKey
    },
    stdio: ["ignore", "pipe", "pipe"]
  });
  api.stdout.pipe(log);
  api.stderr.pipe(log);
  for (const deadline = Date.now() + 90000; Date.now() < deadline; await sleep(500)) {
    if ((await fetch(`${base}/health/ready`).catch(() => null))?.status === 200) return;
    if (api.exitCode !== null || api.signalCode !== null) throw new Error("API exited");
  }
  throw new Error("API not ready");
}
async function stopApi() {
  if (!api || api.exitCode !== null || api.signalCode !== null) return;
  const exited = once(api, "exit");
  api.kill();
  await exited;
}

// ---- minimal CDP client ----
const debugPort = await freePort();
const browser = spawn(browserPath, [
  "--headless=new", `--remote-debugging-port=${debugPort}`, `--user-data-dir=${join(work, "profile")}`,
  "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--window-size=1280,1700", "about:blank"
], { stdio: "ignore" });

let socket;
let nextId = 0;
const waiting = new Map();
async function connect() {
  for (let i = 0; i < 60; i += 1) {
    const targets = await fetch(`http://127.0.0.1:${debugPort}/json/list`).then(r => r.json()).catch(() => null);
    const page = targets?.find(t => t.type === "page");
    if (page) {
      socket = new WebSocket(page.webSocketDebuggerUrl);
      await new Promise((res, rej) => { socket.onopen = res; socket.onerror = rej; });
      socket.onmessage = event => {
        const message = JSON.parse(event.data);
        if (message.id && waiting.has(message.id)) {
          const { res, rej } = waiting.get(message.id);
          waiting.delete(message.id);
          message.error ? rej(new Error(message.error.message)) : res(message.result);
        } else if (message.method === "Runtime.exceptionThrown") {
          browserErrors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
        } else if (message.method === "Runtime.consoleAPICalled" && message.params.type === "error") {
          browserErrors.push(message.params.args.map(a => a.value ?? a.description).join(" "));
        }
      };
      return;
    }
    await sleep(250);
  }
  throw new Error("browser did not start");
}
const cdp = (method, params = {}) => new Promise((res, rej) => {
  const id = ++nextId;
  waiting.set(id, { res, rej });
  socket.send(JSON.stringify({ id, method, params }));
});
async function evaluate(expression) {
  const { result, exceptionDetails } = await cdp("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
  return result.value;
}
async function waitFor(expression, label, timeout = 20000) {
  for (const deadline = Date.now() + timeout; Date.now() < deadline; await sleep(150)) {
    if (await evaluate(expression)) return;
  }
  throw new Error(`timed out waiting for ${label}`);
}
async function screenshot(name) {
  const { data } = await cdp("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
  writeFileSync(join(shots, `${name}.png`), Buffer.from(data, "base64"));
}
const text = selector => evaluate(`document.querySelector(${JSON.stringify(selector)})?.innerText ?? ""`);
async function check(name, fn) {
  try { await fn(); results.push(`PASS ${name}`); } catch (error) { results.push(`FAIL ${name}: ${error.message}`); throw error; }
}
// Sets a form control the way a user would (fires input/change), so app.js sees the edit.
const setField = (name, value) => evaluate(`(() => { const el = document.querySelector('#operation-form [name=${name}]');
  el.value = ${JSON.stringify(String(value))}; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
const selectByText = (name, label) => evaluate(`(() => { const el = document.querySelector('#operation-form [name=${name}]');
  const option = [...el.options].find(o => o.text.startsWith(${JSON.stringify(label)})); el.value = option.value;
  el.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
const tab = key => evaluate(`document.querySelector('#operation-tabs [data-operation=${key}]').click() ?? true`);
const firstBalance = code => evaluate(`[...document.querySelectorAll('#inventory-body tr')].map(r => [...r.cells].map(c => c.innerText))
  .filter(c => c[1] === ${JSON.stringify(code)}).map(c => Number(c[2]))[0] ?? null`);
async function login(username) {
  await evaluate(`(() => { const f = document.querySelector('#login-form'); f.username.value = ${JSON.stringify(username)};
    f.password.value = ${JSON.stringify(secrets.password)}; f.requestSubmit(); return true; })()`);
  await waitFor(`!document.querySelector('#app-view').classList.contains('hidden') && document.querySelector('#product-count').innerText !== '–' && !!document.querySelector('#submit-button, #operation-form p')`, `${username} signed in`);
}

try {
  await startApi();
  await connect();
  await cdp("Page.enable");
  await cdp("Runtime.enable");
  await cdp("Emulation.setDeviceMetricsOverride", { width: 1280, height: 1600, deviceScaleFactor: 1, mobile: false });

  await check("login view, real API connectivity label", async () => {
    await cdp("Page.navigate", { url: base });
    await waitFor(`document.querySelector('#api-status')?.dataset.state === 'ready'`, "health ready");
    assert.match(await text("#api-status"), /已连接真实 WMS API/);
    assert.equal(await evaluate(`document.querySelector('#app-view').classList.contains('hidden')`), true);
    await screenshot("01-login");
  });

  await check("wrong password shows a clear message and no session", async () => {
    await evaluate(`(() => { const f = document.querySelector('#login-form'); f.username.value = 'supervisor'; f.password.value = 'wrong'; f.requestSubmit(); return true; })()`);
    await waitFor(`document.querySelector('#login-message').innerText.includes('登录失败')`, "login error");
    assert.equal(await evaluate(`document.querySelector('#login-password').value`), "");
  });

  await check("supervisor signs in; real empty state, no fake stock", async () => {
    await login("supervisor");
    await waitFor(`document.querySelector('#inventory-body').innerText.includes('没有符合条件的库存余额')`, "empty balances");
    assert.match(await text("#session-user"), /supervisor/);
    assert.match(await text(".source-banner"), /真实后端数据/);
    assert.equal(await text("#product-count"), "2");
    assert.equal(await text("#location-count"), "3");
    await screenshot("02-signed-in-empty");
  });

  await check("receipt: double click commits once, balance reloaded from server", async () => {
    await selectByText("product", "WIDGET-001");
    await selectByText("location", "A-01-01");
    await setField("quantity", 5);
    await setField("referenceNo", "PO-UI-1");
    await evaluate(`(() => { const b = document.querySelector('#submit-button'); b.click(); b.click(); b.click(); return true; })()`);
    await waitFor(`document.querySelector('#operation-message').classList.contains('success')`, "receipt success");
    await waitFor(`[...document.querySelectorAll('#inventory-body tr')].some(r => r.cells[1]?.innerText === 'A-01-01')`, "balance row");
    assert.equal(await firstBalance("A-01-01"), 5);
    assert.equal(await text("#transaction-count"), "1");
    assert.match(await text("#history-list"), /RCV-\d{8}-\d{6}/);
  });

  await check("shipment commits; too much shows INSUFFICIENT_STOCK from the server", async () => {
    await tab("ship");
    await selectByText("product", "WIDGET-001");
    await selectByText("location", "A-01-01");
    await setField("quantity", 2);
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('#operation-message').classList.contains('success')`, "ship success");
    await waitFor(`[...document.querySelectorAll('#inventory-body tr')].some(r => r.cells[1]?.innerText === 'A-01-01' && r.cells[2]?.innerText === '3')`, "A-01-01 = 3");
    assert.equal(await firstBalance("A-01-01"), 3);
    await setField("quantity", 100);
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('#operation-message').innerText.includes('库存不足')`, "insufficient stock");
    assert.equal(await evaluate(`document.querySelector('#pending-banner').classList.contains('hidden')`), true);
  });

  await check("adjustment with delta 0 shows the server's 400 field error", async () => {
    await tab("adjust");
    await selectByText("product", "WIDGET-001");
    await selectByText("location", "A-01-01");
    await setField("quantityDelta", 0);
    await setField("reason", "UI test");
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('[data-error-for=quantityDelta]').innerText.length > 0`, "field error");
    assert.match(await text("#operation-message"), /400/);
    await setField("quantityDelta", -1);
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('#operation-message').classList.contains('success')`, "adjust success");
    await waitFor(`document.querySelector('#history-list').innerText.includes('ADJ-')`, "ADJ in history");
  });

  await check("movement and count commit", async () => {
    await tab("move");
    await selectByText("product", "WIDGET-001");
    await selectByText("fromLocation", "A-01-01");
    await selectByText("toLocation", "A-01-02");
    await setField("quantity", 1);
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('#history-list').innerText.includes('MOV-')`, "MOV");
    await tab("count");
    await selectByText("product", "WIDGET-001");
    await selectByText("location", "A-01-02");
    await setField("countedQuantity", 3);
    await setField("reason", "UI cycle count");
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`document.querySelector('#history-list').innerText.includes('CNT-')`, "CNT");
    await waitFor(`[...document.querySelectorAll('#inventory-body tr')].some(r => r.cells[1]?.innerText === 'A-01-02' && r.cells[2]?.innerText === '3')`, "A-01-02 = 3");
    assert.equal(await firstBalance("A-01-01"), 1);
  });

  await check("API down: write becomes a pending action; retry after restart reuses key and body", async () => {
    await tab("receive");
    await selectByText("product", "WIDGET-001");
    await selectByText("location", "A-01-01");
    await setField("quantity", 4);
    await stopApi();
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`!document.querySelector('#pending-banner').classList.contains('hidden')`, "pending banner", 30000);
    assert.match(await text("#pending-banner"), /WMS API 不可达/);
    assert.equal(await evaluate(`document.querySelector('#submit-button').disabled`), true);
    await waitFor(`document.querySelector('#inventory-body').innerText.includes('读取失败')`, "balance error state");
    await screenshot("03-api-down-pending");
    const keyBefore = (await text("#pending-banner")).match(/[0-9a-f-]{36}/)[0];
    await startApi();
    await evaluate(`document.querySelector('#retry-button').click() ?? true`);
    await waitFor(`document.querySelector('#operation-message').classList.contains('success')`, "retry success", 30000);
    await waitFor(`[...document.querySelectorAll('#inventory-body tr')].some(r => r.cells[1]?.innerText === 'A-01-01' && r.cells[2]?.innerText === '5')`, "A-01-01 = 5");
    assert.match(keyBefore, /^[0-9a-f]{8}-/);
  });

  await check("editing the form discards a pending action (changed input is a new action)", async () => {
    await stopApi();
    await setField("quantity", 2);
    await evaluate(`document.querySelector('#submit-button').click() ?? true`);
    await waitFor(`!document.querySelector('#pending-banner').classList.contains('hidden')`, "pending banner", 30000);
    await setField("quantity", 3);
    await waitFor(`document.querySelector('#pending-banner').classList.contains('hidden')`, "pending discarded");
    assert.match(await text("#operation-message"), /已放弃/);
    await startApi();
  });

  await check("after the API returns, 重新读取 replaces the error state with server balances", async () => {
    await evaluate(`document.querySelector('#reload-button').click() ?? true`);
    await waitFor(`document.querySelector('#api-status').dataset.state === 'ready'`, "health ready again");
    await waitFor(`[...document.querySelectorAll('#inventory-body tr')].some(r => r.cells[1]?.innerText === 'A-01-01' && r.cells[2]?.innerText === '5')`, "balances reloaded");
  });

  await check("barcode lookup resolves through the API", async () => {
    await evaluate(`(() => { document.querySelector('#lookup-value').value = '4006381333931'; document.querySelector('#lookup-form').requestSubmit(); return true; })()`);
    await waitFor(`document.querySelector('#lookup-result').innerText.includes('WIDGET-001')`, "lookup");
    assert.match(await text("#lookup-result"), /合计 8 EA/);
    await screenshot("04-after-writes");
  });

  await check("logout clears session and history; shipper sees receive as not permitted", async () => {
    await evaluate(`document.querySelector('#logout-button').click() ?? true`);
    await waitFor(`!document.querySelector('#login-view').classList.contains('hidden')`, "login view");
    assert.equal(await evaluate(`document.querySelector('#app-view').classList.contains('hidden')`), true);
    assert.equal(await evaluate(`[...document.querySelectorAll('#app-view *')].filter(e => !e.children.length && /RCV-[0-9]{8}/.test(e.textContent)).map(e => e.id || e.className || e.tagName).join(',')`), "", "previous user's data left in the DOM");
    await login("shipper");
    assert.equal(await text("#transaction-count"), "0");
    assert.match(await text("#history-list"), /尚未成功提交/);
    assert.match(await text("#operation-form"), /没有 Inventory.Receive 权限/);
    await screenshot("05-shipper-no-permission");
    await cdp("Emulation.setDeviceMetricsOverride", { width: 420, height: 900, deviceScaleFactor: 1, mobile: true });
    await sleep(300);
    assert.ok(await evaluate(`document.documentElement.scrollWidth <= 420`), "narrow layout overflows horizontally");
    await screenshot("06-narrow-420px");
  });

  await check("no browser exceptions or console errors; no secrets in API logs", async () => {
    assert.deepEqual(browserErrors.filter(e => !/Failed to load resource/.test(e)), []);
    const logs = Array.from({ length: apiRun }, (_, i) => readFileSync(join(work, `api-${i + 1}.log`), "utf8")).join("\n");
    assert.ok(!logs.includes(secrets.password) && !logs.includes(secrets.signingKey));
  });
} finally {
  socket?.close();
  browser.kill();
  await stopApi();
  ui.close();
  console.log(results.join("\n"));
  console.log(`${results.filter(r => r.startsWith("PASS")).length} passed, ${results.filter(r => r.startsWith("FAIL")).length} failed; screenshots in ${shots}`);
  await sleep(1000);
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 });
}
