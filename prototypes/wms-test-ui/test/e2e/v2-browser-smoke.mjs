// UI contract-fixture tests ONLY. No real WMS API or database is started or changed.
// Run: node test/e2e/v2-browser-smoke.mjs
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { createAppServer, loadConfig } from "../../server.mjs";

const executable = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Google/Chrome/Application/chrome.exe"].find(existsSync);
if (!executable) throw new Error("No existing Edge/Chrome available; do not install one automatically.");
const root = fileURLToPath(new URL("../../../../", import.meta.url));
const shots = join(root, "artifacts", "v2-ui"); mkdirSync(shots, { recursive: true });
const work = mkdtempSync(join(tmpdir(), "wms-v2-browser-"));
const token = randomBytes(24).toString("base64url"), password = randomBytes(18).toString("base64url");
const user = { id: "1", username: "fixture", displayName: "测试操作员", roles: ["systemAdministrator", "receivingClerk"], currentRole: "systemAdministrator" };
const products = [{ id: "1", name: "测试商品", sku: "SKU-000001", barcode: "BC-000001", unitId: "1", unitName: "件", decimalPlaces: 0, serialTracking: false, categoryIds: [], totalQuantity: "2", version: 1 }];
const bins = [{ id: "1", zone: "A", rack: "B", level: "1", cell: "1", code: "A-B-1-01", quantity: "2", capacity: "10", occupancyPercent: "20", productName: "测试商品", unitName: "件" }];
const calls = [], errors = [], results = [];
let uncertainOnce = true, delayNextProductRead = false;
async function listen(server) { server.listen(0, "127.0.0.1"); await once(server, "listening"); return `http://127.0.0.1:${server.address().port}`; }
const backend = createServer((req, res) => {
  let text = ""; req.on("data", c => text += c); req.on("end", () => {
    const url = new URL(req.url, "http://fixture"); const body = text ? JSON.parse(text) : {};
    calls.push({ path: url.pathname, query: url.search, method: req.method, body, key: req.headers["idempotency-key"] });
    res.setHeader("Content-Type", "application/json");
    const send = value => res.end(JSON.stringify(value));
    if (url.pathname.endsWith("/auth/login")) {
      if (body.password !== password) { res.statusCode = 401; return send({ title: "用户名或密码不正确。" }); }
      return send({ token, expiresAt: new Date(Date.now() + 3600000).toISOString(), user });
    }
    if (req.headers.authorization !== `Bearer ${token}`) { res.statusCode = 401; return send({ code: "UNAUTHENTICATED" }); }
    if (url.pathname.endsWith("/auth/me")) return send({ user });
    if (url.pathname.endsWith("/auth/switch-role")) { user.currentRole = body.role; return send({ user }); }
    if (url.pathname.endsWith("/dashboard")) return send({ skuCount: 1, openAlertCount: 0, activeCountCount: 0, unresolvedInvestigationCount: 0, scope: url.searchParams.get("scope") || "all", recentActivity: [], alerts: [] });
    if (url.pathname.endsWith("/products")) return send({ items: products });
    if (url.pathname.endsWith("/bins")) return send({ items: bins });
    if (url.pathname.endsWith("/units")) return send({ items: [{ id: "1", name: "件" }] });
    if (url.pathname.endsWith("/categories")) return send({ items: [] });
    if (url.pathname === "/api/v2/serialized-items" && req.method === "GET") return send({ items: [
      { id: "1", serialNumber: "SN-000001", productId: "1", binId: "1", inStock: true },
      { id: "2", serialNumber: "SN-000002", productId: "1", binId: "1", inStock: true },
      { id: "3", serialNumber: "SN-OTHER-BIN", productId: "1", binId: "2", inStock: true }
    ] });
    if (url.pathname === "/api/v2/products/1" && req.method === "GET") {
      const value = { ...products[0], bins, imageContentType: "image/png", imageBase64: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV1cAAAAASUVORK5CYII=" };
      if (delayNextProductRead) { delayNextProductRead = false; return setTimeout(() => send(value), 300); }
      return send(value);
    }
    if (url.pathname === "/api/v2/counts" && req.method === "GET") return send({ items: [{ id: "1", number: "CT-000001", status: "inProgress", binCode: bins[0].code }] });
    if (url.pathname === "/api/v2/counts/1" && req.method === "GET") return send({ id: "1", number: "CT-000001", status: "inProgress", round: 2, version: 4, allowedActions: ["submit", "cancel"], bookQuantity: "sensitive-current-round", currentSnapshot: { quantity: "must-stay-hidden" }, results: [{ id: "99", round: 1, bookQuantity: "7", actualQuantity: "5", difference: "-2", unitName: "件", disposition: "underInvestigation", submittedByName: "此前盘点员", submittedByRole: "warehouseSupervisor" }] });
    if (url.pathname === "/api/v2/activity-logs") return send({ items: [{ id: "1", occurredAt: "2026-10-19T18:00:00Z", actorName: "<img src=x onerror=alert(1)>", actorRole: "warehouseSupervisor", type: "productUpdate", description: "更名", success: true, subjects: [{ type: "product", id: "1" }], changes: [{ field: "name", before: "旧名称", after: "新名称" }, { field: "password", before: "hidden-before-fixture", after: "hidden-after-fixture" }] }] });
    if (url.pathname === "/api/v2/alerts") return send({ items: [{ id: "1", type: "lowStock", status: "open", productName: "测试商品", binCode: bins[0].code, createdAt: "2026-10-19T18:00:00Z", message: "测试提醒", trigger: { quantity: "1", capacity: "10", occupancyPercent: "10", lowThreshold: "20", highThreshold: "80" } }] });
    if (url.pathname === "/api/v2/serialized-items/1") return send({ id: "1", serialNumber: "SN-NEW", historicalSerialNumbers: ["SN-OLD"], history: [{ transactionNumber: "TX-000001", type: "receipt", serialNumber: "SN-OLD", actorName: "操作员", actorRole: "receivingClerk", toBinCode: bins[0].code }], corrections: [{ activityLogId: "99", previousSerialNumber: "SN-OLD", newSerialNumber: "SN-NEW", reason: "修正录入", actorName: "主管", actorRole: "warehouseSupervisor" }] });
    if (url.pathname.endsWith("/settings")) return send({ defaultRackLevels: 3, defaultCellsPerLevel: 4, lowThreshold: "20", highThreshold: "80", sessionHours: 8 });
    if (url.pathname === "/api/v2/inventory/receipts" && uncertainOnce) { uncertainOnce = false; res.statusCode = 502; return send({ code: "API_UNAVAILABLE", title: "测试：提交结果暂时未知" }); }
    return send(req.method === "GET" ? { items: [] } : { id: "1", number: "TX-000001" });
  });
});
const backendBase = await listen(backend);
const ui = createAppServer(loadConfig({ WMS_API_BASE_URL: backendBase }), { logger: { log() {} } }); const base = await listen(ui);
const probe = createServer(); const debugBase = await listen(probe); const debugPort = probe.address().port; await new Promise(r => probe.close(r));
const browser = spawn(executable, ["--headless=new", `--remote-debugging-port=${debugPort}`, `--user-data-dir=${join(work, "profile")}`, "--no-first-run", "--no-default-browser-check", "--disable-extensions", "about:blank"], { stdio: "ignore", windowsHide: true });
let socket, seq = 0; const pending = new Map();
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function connect() {
  for (let count = 0; count < 80; count++) {
    const targets = await fetch(debugBase + "/json/list").then(r => r.json()).catch(() => []);
    const page = targets.find(p => p.type === "page");
    if (page) {
      socket = new WebSocket(page.webSocketDebuggerUrl); await once(socket, "open");
      socket.onmessage = event => { const m = JSON.parse(event.data); if (m.id) { const item = pending.get(m.id); if (item) { pending.delete(m.id); m.error ? item.reject(new Error(m.error.message)) : item.resolve(m.result); } } else if (m.method === "Runtime.exceptionThrown") errors.push(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text); };
      return;
    } await pause(150);
  } throw new Error("Browser did not start");
}
function cdp(method, params = {}) { return new Promise((resolve, reject) => { const id = ++seq; pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params })); }); }
async function evaluate(expression) { const value = await cdp("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }); if (value.exceptionDetails) throw new Error(value.exceptionDetails.exception?.description || value.exceptionDetails.text); return value.result.value; }
async function wait(expression, label) { for (let count = 0; count < 100; count++) { if (await evaluate(expression)) return; await pause(100); } throw new Error("Timed out: " + label); }
async function route(page) { const titles = { products: "商品与库存", operations: "出入库操作", counts: "盘点与调查", settings: "系统设置", "serialized-items": "实物追踪", "activity-logs": "操作历史", alerts: "异常提醒", bins: "库位" }; await evaluate(`location.hash=${JSON.stringify(page)}`); await wait(`document.querySelector('#page-title').textContent===${JSON.stringify(titles[page])} && document.querySelector('#page').getAttribute('aria-busy')==='false'`, page); }
async function click(selector) { await evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`); }
async function field(selector, value) { await evaluate(`(()=>{const el=document.querySelector(${JSON.stringify(selector)});el.value=${JSON.stringify(value)};el.dispatchEvent(new Event('change',{bubbles:true}));})()`); }
async function shot(name) { await wait("document.readyState==='complete' && document.documentElement.clientWidth>0", "page layout"); const { data } = await cdp("Page.captureScreenshot", { format: "png", captureBeyondViewport: false }); writeFileSync(join(shots, name + ".png"), Buffer.from(data, "base64")); }
async function check(name, fn) { await fn(); results.push(name); console.log("PASS " + name); }
try {
  await connect(); await cdp("Page.enable"); await cdp("Runtime.enable");
  await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
  await cdp("Page.navigate", { url: base + "/v2/" }); await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login page");
  await check("desktop login and no browser-stored API credential", async () => {
    await shot("01-login"); await field("#login-form [name=username]", "fixture"); await field("#login-form [name=password]", password);
    await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && !!document.querySelector('.stat strong')", "dashboard");
    assert.equal(await evaluate("document.querySelector('.stat strong').textContent"), "1");
    assert.equal(await evaluate("localStorage.length + sessionStorage.length"), 0);
    assert.ok(!(await evaluate("document.cookie")).includes(token)); await shot("02-dashboard");
  });
  await check("valid session is restored after reload", async () => {
    await cdp("Page.reload"); await wait("!!document.querySelector('.stat strong') && !document.querySelector('#workspace').hidden", "restored session");
  });
  await check("dashboard scope keeps the selected identity of the displayed results", async () => {
    await field("#activity-scope", "mine");
    await wait("!!document.querySelector('#activity-scope') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "mine dashboard");
    assert.equal(await evaluate("document.querySelector('#activity-scope').value"), "mine");
    assert.ok(calls.some(c => c.path === "/api/v2/dashboard" && c.query.includes("scope=mine")));
  });
  await check("product editing sends PUT to the existing record", async () => {
    await route("products"); const before = calls.length; await click('[data-action="edit"][data-id="1"]'); await wait("document.querySelector('#editor').open", "product editor");
    assert.ok(!calls.slice(before).some(c => c.path === "/api/v2/bins"));
    assert.equal(await evaluate("document.querySelector('#editor-form [name=name]').value"), "测试商品");
    await wait("document.querySelector('#product-image-preview img')?.naturalWidth > 0", "current product image");
    await field("#editor-form [name=name]", "测试商品更名"); await evaluate("document.querySelector('#editor-form').requestSubmit()");
    await wait("!document.querySelector('#editor').open", "saved editor");
    assert.ok(calls.some(c => c.method === "PUT" && c.path === "/api/v2/products/1" && c.body.name === "测试商品更名"));
    assert.equal(calls.find(c => c.method === "PUT" && c.path === "/api/v2/products/1").body.version, 1);
  });
  await check("a superseded detail response cannot replace a newer editor", async () => {
    await route("products");
    const reads = () => calls.filter(c => c.method === "GET" && c.path === "/api/v2/products/1").length;
    const before = reads(); delayNextProductRead = true;
    await click('[data-action="detail"][data-id="1"]');
    for (let i = 0; i < 100 && reads() === before; i++) await pause(5);
    assert.ok(reads() > before);
    await click('[data-close]'); await click('[data-action="edit"][data-id="1"]');
    await wait("document.querySelector('#editor').open && !!document.querySelector('#editor-form [name=name]')", "new editor");
    await pause(450);
    assert.equal(await evaluate("!!document.querySelector('#editor-form [name=name]')"), true);
    assert.equal(await evaluate("document.querySelector('#editor-form button[type=submit]').hidden"), false);
    await click('[data-close]');
  });
  await check("QR / barcode previews encode the same current product barcode and print only labels", async () => {
    await route("products"); await click('[data-action="label"][data-id="1"]');
    await wait("!!document.querySelector('#label-preview svg')", "QR preview");
    assert.equal(await evaluate("document.querySelector('#label-preview span').textContent"), "BC-000001");
    assert.match(await evaluate("document.querySelector('#label-preview svg').getAttribute('aria-label')"), /二维码 BC-000001/);
    await field("#label-format", "code128");
    assert.match(await evaluate("document.querySelector('#label-preview svg').getAttribute('aria-label')"), /一维条码 BC-000001/);
    await evaluate("window.print=()=>{}"); await field("#label-copies", "2"); await click('[data-action="print-label"]');
    assert.equal(await evaluate("document.querySelectorAll('body>.print-only .print-label').length"), 2);
    await evaluate("window.dispatchEvent(new Event('afterprint'))");
    await field("#label-format", "qr"); await shot("05-label-qr"); await click("#editor [data-close]");
  });
  await check("removing an existing image is an explicit update, not an accidental empty upload", async () => {
    await click('[data-action="edit"][data-id="1"]'); await wait("!!document.querySelector('[name=removeImage]')", "remove-image field");
    await evaluate("const box=document.querySelector('[name=removeImage]');box.checked=true;box.dispatchEvent(new Event('change',{bubbles:true}))");
    assert.equal(await evaluate("document.querySelector('#editor-form [name=image]').disabled"), true);
    await evaluate("document.querySelector('#editor-form').requestSubmit()"); await wait("!document.querySelector('#editor').open", "image removal request");
    const update = calls.filter(c => c.path === "/api/v2/products/1" && c.method === "PUT").at(-1);
    assert.equal(update.body.removeImage, true); assert.equal(update.body.imageBase64, undefined);
  });
  await check("ordinary receipt is not blocked by hidden inline-product fields", async () => {
    await route("operations"); await field('#operation-form [name=productId]', "1"); await field('#operation-form [name=toBinId]', "1"); await field('#operation-form [name=quantity]', "2");
    assert.equal(await evaluate("document.querySelector('#operation-form').checkValidity()"), true);
    await evaluate("document.querySelector('#operation-form').requestSubmit()"); await wait("!document.querySelector('#pending').hidden", "uncertain write");
    const first = calls.find(c => c.path === "/api/v2/inventory/receipts"); assert.equal(first.body.lines[0].quantity, "2");
    await click('[data-action="retry"]'); await wait("document.querySelector('#pending').hidden", "retried write");
    const writes = calls.filter(c => c.path === "/api/v2/inventory/receipts"); assert.equal(writes.length, 2); assert.equal(writes[0].key, writes[1].key); assert.deepEqual(writes[0].body, writes[1].body);
  });
  await check("blind count detail hides the current round book quantity", async () => {
    await route("counts"); await click('[data-action="detail"][data-id="1"]'); await wait("document.querySelector('#editor').open", "count detail");
    const text = await evaluate("document.querySelector('#editor-fields').textContent"); assert.ok(!text.includes("sensitive-current-round")); assert.ok(!text.includes("must-stay-hidden"));
    assert.ok(text.includes("此前盘点员")); assert.ok(text.includes("待调查")); assert.ok(text.includes("-2"));
    await click('[data-action="count-submit"]'); await wait("!!document.querySelector('#editor-form [name=actualQuantity]')", "count submission form");
    await field('#editor-form [name=actualQuantity]', "2"); await evaluate("document.querySelector('#editor-form').requestSubmit()"); await wait("!document.querySelector('#editor').open", "submitted count");
    assert.ok(calls.some(c => c.path === "/api/v2/counts/1/submit" && c.body.actualQuantity === "2" && c.body.round === 2 && c.body.version === 4));
  });
  await check("receipt can inline-create a product with a new custom unit", async () => {
    await route("operations"); await field('#operation-form [name=productId]', "__new");
    await field('#operation-form .inline-product [name=name]', "本次新增商品");
    await field('#operation-form [name=newUnitName]', "台");
    await field('#operation-form [name=toBinId]', "1"); await field('#operation-form [name=quantity]', "3");
    assert.equal(await evaluate("document.querySelector('#operation-form').checkValidity()"), true);
    await evaluate("document.querySelector('#operation-form').requestSubmit()");
    await wait("!!document.querySelector('#notice').textContent.includes('操作已提交') && !!document.querySelector('#operation-form')", "inline receipt saved");
    const receipt = calls.filter(c => c.path === "/api/v2/inventory/receipts").at(-1);
    assert.equal(receipt.body.lines[0].newProduct.newUnitName, "台"); assert.equal(receipt.body.lines[0].newProduct.unitId, null);
  });
  await check("bin diagram and small-screen layout remain usable", async () => {
    await route("bins");
    await field('#filter-form [name=zone]', "A"); await field('#filter-form [name=rack]', "B");
    await evaluate("document.querySelector('#filter-form').requestSubmit()");
    await wait("!!document.querySelector('#filter-form') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "filtered bins");
    assert.equal(await evaluate("document.querySelector('#filter-form [name=rack]').value"), "B");
    assert.ok(calls.some(c => c.path === "/api/v2/bins" && c.query.includes("zone=A") && c.query.includes("rack=B")));
    await click('[data-action="toggle-grid"]'); assert.equal(await evaluate("document.querySelectorAll('.bin-card').length"), 1); await shot("03-bins");
    await cdp("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 1, mobile: true }); await shot("04-mobile");
    assert.equal(await evaluate("document.documentElement.scrollWidth <= innerWidth"), true);
  });
  await check("shipment picker selects current serials only from the selected bin", async () => {
    products[0].serialTracking = true;
    await route("operations"); await click('[data-action="operation"][data-id="shipments"]');
    await field('#operation-form [name=productId]', "1"); await field('#operation-form [name=fromBinId]', "1");
    await click('[data-action="pick-serials"]'); await wait("document.querySelectorAll('[name=pickedSerial]').length===2", "available serials");
    assert.ok(!(await evaluate("document.querySelector('#editor-fields').textContent")).includes("SN-OTHER-BIN"));
    await evaluate("document.querySelectorAll('[name=pickedSerial]').forEach(n=>n.checked=true)");
    await click('[data-action="apply-serials"]'); await evaluate("document.querySelector('#operation-form').requestSubmit()");
    await wait("!!document.querySelector('#notice').textContent.includes('操作已提交')", "serial shipment saved");
    const request = calls.filter(c => c.path === "/api/v2/inventory/shipments").at(-1);
    assert.deepEqual(request.body.lines[0].serialNumbers, ["SN-000001", "SN-000002"]);
    await wait("!!document.querySelector('#operation-form [name=fromBinId]') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "shipment tab preserved after save");
    assert.equal(await evaluate("document.querySelector('.tabs .selected').dataset.id"), "shipments");
    products[0].serialTracking = false;
  });
  await check("investigation adjustment links an exact result ID and rejects invalid IDs", async () => {
    await route("operations"); await click('[data-action="operation"][data-id="adjustments"]');
    await field('#operation-form [name=productId]', "1"); await field('#operation-form [name=toBinId]', "1");
    await field('#operation-form [name=quantity]', "-1"); await field('#operation-form [name=reason]', "核实当前库存后修正");
    await field('#operation-form [name=countResultId]', "1.5");
    const before = calls.filter(c => c.path === "/api/v2/inventory/adjustments").length;
    await evaluate("document.querySelector('#operation-form').requestSubmit()");
    await wait("document.querySelector('#notice').textContent.includes('关联盘点结果 ID')", "invalid result ID rejected");
    assert.equal(calls.filter(c => c.path === "/api/v2/inventory/adjustments").length, before);
    await field('#operation-form [name=countResultId]', "9007199254740993");
    await evaluate("document.querySelector('#operation-form').requestSubmit()");
    await wait("document.querySelector('#notice').textContent.includes('操作已提交')", "linked adjustment");
    const request = calls.filter(c => c.path === "/api/v2/inventory/adjustments").at(-1);
    assert.equal(request.body.countResultId, "9007199254740993"); assert.equal(request.body.lines[0].quantity, "-1");
    await wait("!!document.querySelector('#operation-form [name=reason]') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "adjustment tab preserved after save");
    assert.equal(await evaluate("document.querySelector('.tabs .selected').dataset.id"), "adjustments");
  });
  await check("read-only operation evidence escapes names and hides sensitive changes", async () => {
    await route("activity-logs"); await click('[data-action="log-detail"][data-id="1"]');
    await wait("document.querySelector('#editor').open", "operation evidence");
    const text = await evaluate("document.querySelector('#editor-fields').textContent");
    assert.ok(text.includes("<img src=x onerror=alert(1)>")); assert.ok(text.includes("仓库主管")); assert.ok(text.includes("旧名称"));
    assert.ok(!text.includes("hidden-before-fixture")); assert.ok(!text.includes("hidden-after-fixture"));
    assert.equal(await evaluate("document.querySelectorAll('#editor-fields img').length"), 0);
    assert.equal(await evaluate("document.querySelector('#editor-form button[type=submit]').hidden"), true);
    await click('[data-close]');
  });
  await check("alert details identify the immutable trigger snapshot", async () => {
    await route("alerts"); await click('[data-action="alert-detail"][data-id="1"]');
    await wait("document.querySelector('#editor').open", "alert evidence");
    assert.ok((await evaluate("document.querySelector('#editor-fields').textContent")).includes("触发时快照（不是当前库存）"));
    await click('[data-close]');
  });
  await check("serial trace shows the historical number separately from corrections", async () => {
    await route("serialized-items"); await click('[data-action="detail"][data-id="1"]');
    await wait("document.querySelector('#editor').open", "serial evidence");
    const text = await evaluate("document.querySelector('#editor-fields').textContent");
    assert.ok(text.includes("SN-OLD → SN-NEW")); assert.ok(text.includes("TX-000001")); assert.ok(text.includes("旧号码不可用于库存操作"));
    await click('[data-close]');
  });
  await check("switching to receiving role does not inherit administrator controls", async () => {
    await field("#role-select", "receivingClerk"); await wait("!document.querySelector('#navigation a[href=\"#users\"]')", "role controls");
    await route("operations"); const text = await evaluate("document.querySelector('.tabs').textContent"); assert.match(text, /入库/); assert.ok(!text.includes("移库")); assert.ok(!text.includes("调整"));
  });
  await check("offline backend shows an error, not fake authenticated stock", async () => {
    backend.closeAllConnections(); await new Promise(resolve => backend.close(resolve));
    await cdp("Page.reload"); await wait("!!document.querySelector('#login-error')?.textContent", "offline status");
    assert.equal(await evaluate("document.querySelector('#workspace').hidden"), true);
    assert.match(await evaluate("document.querySelector('#login-error').textContent"), /无法连接|未就绪/);
  });
  assert.deepEqual(errors, []); console.log(`PASS ${results.length} browser contract-fixture checks; NOT real-backend acceptance`);
} finally {
  socket?.close(); const ended = once(browser, "exit"); browser.kill(); await Promise.race([ended, pause(3000)]);
  ui.closeAllConnections(); backend.closeAllConnections(); ui.close(); backend.close();
  // Only this freshly-created, validated temporary directory can be removed.
  const within = relative(resolve(tmpdir()), resolve(work));
  if (within && !within.startsWith("..") && !within.includes(":") && within.startsWith("wms-v2-browser-")) { try { rmSync(work, { recursive: true, force: true }); } catch { /* Browser may still be releasing its temporary profile. */ } }
}
