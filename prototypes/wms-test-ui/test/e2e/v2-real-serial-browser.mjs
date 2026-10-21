// Run after B3b exists. Real serial query/correction + physical picker acceptance, using a temporary warehouse only.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

let total = 0;
const failures = [];
async function check(name, action) {
  try { await action(); total++; console.log("PASS " + name); }
  catch (error) { failures.push(name); console.error("FAIL " + name + ": " + error.message); }
}
await withRealV2(async ({ repo, work, base, password, login, request }) => {
  const cookie = await login("supervisor");
  const get = async path => { const response = await request(path, { cookie }); assert.equal(response.status, 200); return response.json(); };
  const post = (path, body, identity = cookie) => request(path, { method: "POST", body, cookie: identity, key: randomUUID() });
  const created = async (path, body) => { const response = await post(path, body); assert.equal(response.status, 201); return response.json(); };
  const problem = async (response, status, code) => { assert.equal(response.status, status); const body = await response.json(); assert.equal(body.code, code); return body; };
  const unit = (await get("units")).items.find(x => x.name === "件");
  const product = await created("products", { name: "追踪设备", unitId: unit.id, decimalPlaces: 0, serialTracking: true, categoryIds: [] });
  const a = await created("bins", { zone: "追踪区", rack: "A", level: "01", cell: "01" });
  const b = await created("bins", { zone: "追踪区", rack: "A", level: "01", cell: "02" });
  const receipt = await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: a.id, capacity: "10", serialNumbers: ["SN-原始-α", "SN-另一件"] }] });
  const itemId = receipt.lines[0].items.find(x => x.serialNumber === "SN-原始-α").serializedItemId;
  let item;
  await check("real serial list filters by exact product/bin/in-stock and exposes persisted history", async () => {
    const page = await get(`serialized-items?productId=${product.id}&binId=${a.id}&inStock=true`); assert.equal(page.items.length, 2);
    assert.ok(page.items.every(x => typeof x.id === "string" && x.productId === product.id && x.binId === a.id && x.inStock));
    item = await get("serialized-items/" + itemId); assert.equal(item.serialNumber, "SN-原始-α"); assert.equal(item.history.length, 1); assert.equal(item.history[0].transactionId, receipt.id);
    assert.equal((await get(`serialized-items?productId=${product.id}&binId=${b.id}&inStock=true`)).items.length, 0);
  });
  await check("serial correction preserves item identity, stock and immutable receipt evidence", async () => {
    const before = await get("bins/" + a.id);
    const response = await post(`serialized-items/${itemId}/correct`, { serialNumber: "SN-更正-β", reason: "现场核对标签", version: item.version }); assert.equal(response.status, 200); item = await response.json();
    assert.equal(item.id, itemId); assert.equal(item.serialNumber, "SN-更正-β"); assert.ok(item.historicalSerialNumbers.includes("SN-原始-α"));
    assert.equal(item.corrections.length, 1); assert.equal(item.corrections[0].previousSerialNumber, "SN-原始-α"); assert.equal(item.corrections[0].newSerialNumber, "SN-更正-β");
    assert.deepEqual(await get("bins/" + a.id), before); assert.deepEqual(await get("inventory/transactions/" + receipt.id), receipt);
    assert.equal(item.history[0].serialNumber, "SN-原始-α");
  });
  await check("historical serial search identifies the current number but cannot perform inventory writes", async () => {
    const page = await get("serialized-items?search=" + encodeURIComponent("sn-原始-α")); assert.equal(page.items.length, 1); assert.equal(page.items[0].id, itemId);
    assert.equal(page.items[0].matchedHistoricalSerialNumber, "SN-原始-α"); assert.equal(page.items[0].serialNumber, "SN-更正-β");
    const problemBody = await problem(await post("inventory/shipments", { lines: [{ productId: product.id, fromBinId: a.id, serialNumbers: ["SN-原始-α"] }] }), 409, "SERIAL_NUMBER_SUPERSEDED");
    assert.ok(problemBody.subjects?.some(x => x.id === itemId && x.label === "SN-更正-β"));
    await problem(await post("inventory/receipts", { lines: [{ productId: product.id, toBinId: a.id, serialNumbers: ["SN-原始-α"] }] }), 409, "SERIAL_NUMBER_SUPERSEDED");
  });
  await check("correction no-op and optimistic version protection never duplicate history", async () => {
    const version = item.version;
    const same = await post(`serialized-items/${itemId}/correct`, { serialNumber: item.serialNumber, reason: "无需变化", version }); assert.equal(same.status, 200);
    const unchanged = await same.json(); assert.equal(unchanged.version, version); assert.equal(unchanged.corrections.length, 1); assert.deepEqual(unchanged.historicalSerialNumbers, item.historicalSerialNumbers);
    await problem(await post(`serialized-items/${itemId}/correct`, { serialNumber: item.serialNumber, reason: "旧版本不能绕过校验", version: version - 1 }), 409, "VERSION_CONFLICT");
    await problem(await post(`serialized-items/${itemId}/correct`, { serialNumber: "SN-不应写入", reason: "旧页面", version: version - 1 }), 409, "VERSION_CONFLICT");
    const other = (await get("serialized-items?search=" + encodeURIComponent("SN-另一件"))).items[0];
    await problem(await post(`serialized-items/${other.id}/correct`, { serialNumber: "SN-原始-α", reason: "禁止复用旧号", version: other.version }), 409, "ALREADY_EXISTS");
    await problem(await post(`serialized-items/${other.id}/correct`, { serialNumber: item.serialNumber, reason: "禁止复用当前号", version: other.version }), 409, "ALREADY_EXISTS");
  });
  await check("movement, shipment and return preserve corrected identity and each event-time number", async () => {
    const move = await created("inventory/movements", { lines: [{ productId: product.id, fromBinId: a.id, toBinId: b.id, capacity: "8", serialNumbers: [item.serialNumber] }] });
    const ship = await created("inventory/shipments", { lines: [{ productId: product.id, fromBinId: b.id, serialNumbers: [item.serialNumber] }] });
    assert.equal((await get("serialized-items/" + itemId)).inStock, false);
    const returned = await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: b.id, capacity: "6", serialNumbers: [item.serialNumber] }] });
    assert.equal(returned.lines[0].items[0].serializedItemId, itemId);
    item = await get("serialized-items/" + itemId); assert.equal(item.binId, b.id); assert.equal(item.history.length, 4);
    assert.equal(item.history.find(x => x.transactionId === receipt.id).serialNumber, "SN-原始-α");
    for (const tx of [move, ship, returned]) assert.equal(item.history.find(x => x.transactionId === tx.id).serialNumber, "SN-更正-β");
  });
  await check("a receiving clerk can trace serials but cannot correct their identity", async () => {
    const receiver = await login("receiver"); assert.equal((await request("serialized-items/" + itemId, { cookie: receiver })).status, 200);
    await problem(await post(`serialized-items/${itemId}/correct`, { serialNumber: "禁止修改", reason: "越权", version: item.version }, receiver), 403, "FORBIDDEN");
  });
  await check("Unicode serial search matches before cursor pagination and exact bin filters", async () => {
    const receipt = await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: a.id, serialNumbers: ["écrou-01", "ÉCROU-02"] }] });
    const ids = receipt.lines[0].items.map(x => x.serializedItemId);
    for (const search of ["ÉCROU", "écrou"]) {
      const query = `serialized-items?limit=1&productId=${product.id}&binId=${a.id}&inStock=true&search=${encodeURIComponent(search)}`;
      const first = await get(query); assert.equal(first.items.length, 1); assert.ok(first.nextCursor);
      const second = await get(query + "&cursor=" + encodeURIComponent(first.nextCursor)); assert.equal(second.items.length, 1); assert.ok(!second.nextCursor);
      assert.deepEqual(new Set([first.items[0].id, second.items[0].id]), new Set(ids));
    }
    assert.equal((await get(`serialized-items?binId=${b.id}&search=${encodeURIComponent("ÉCROU")}`)).items.length, 0);
  });
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await cdp("Page.navigate", { url: base + "/v2/#serialized-items" }); await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password); await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real serial list");
    await check("real browser corrects a serial and displays original versus current evidence", async () => {
      await click(`[data-action=correct][data-id="${itemId}"]`); await wait("document.querySelector('#editor').open", "serial correction editor");
      await field('#editor-form [name=serialNumber]', "SN-浏览器纠错"); await field('#editor-form [name=reason]', "页面实际核对标签"); await evaluate("document.querySelector('#editor-form').requestSubmit()");
      await wait("!document.querySelector('#editor').open && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real serial correction saved");
      item = await get("serialized-items/" + itemId); assert.equal(item.serialNumber, "SN-浏览器纠错");
      await field('#filter-form [name=search]', "SN-原始-α"); await evaluate("document.querySelector('#filter-form').requestSubmit()");
      await wait("document.querySelector('#page').getAttribute('aria-busy')==='false' && document.querySelector('#page').textContent.includes('旧号')", "historical number warning");
      assert.ok(await evaluate("document.querySelector('#page').textContent.includes('SN-浏览器纠错') && document.querySelector('#page').textContent.includes('请使用当前号码')"));
      await click(`[data-action=detail][data-id="${itemId}"]`); await wait("document.querySelector('#editor').open && document.querySelector('#editor-fields').textContent.includes('SN-浏览器纠错')", "real serial evidence");
      assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('SN-原始-α')"));
      const directory = join(repo, "artifacts", "v2-real-ui"); mkdirSync(directory, { recursive: true });
      const { data } = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(directory, "serials.png"), Buffer.from(data, "base64"));
      await click('[data-close]'); await wait("!document.querySelector('#editor').open", "closed trace");
    });
    await check("real shipment picker selects only persisted current serials in the selected bin", async () => {
      await evaluate("location.hash='operations'"); await wait("!!document.querySelector('#operation-form') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "operation form");
      await click('[data-action=operation][data-id=shipments]'); await field('#operation-form [name=productId]', product.id); await field('#operation-form [name=fromBinId]', b.id);
      await click('[data-action=pick-serials]'); await wait("document.querySelector('#editor').open && document.querySelectorAll('[name=pickedSerial]').length===1", "real serial picker");
      assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('SN-浏览器纠错') && !document.querySelector('#editor-fields').textContent.includes('SN-另一件')"));
      await click('[name=pickedSerial]'); await click('[data-action=apply-serials]'); await evaluate("document.querySelector('#operation-form').dataset.beforeSave='true';document.querySelector('#operation-form').requestSubmit()");
      await wait("!!document.querySelector('#operation-form') && !document.querySelector('#operation-form').dataset.beforeSave && document.querySelector('#page').getAttribute('aria-busy')==='false'", "picked serial shipped");
      assert.equal((await get("serialized-items/" + itemId)).inStock, false); assert.equal((await get("bins/" + b.id)).quantity, "0");
    });
  });
});
assert.deepEqual(failures, [], "Every real serial acceptance check must pass");
console.log(`PASS ${total} REAL serial/API/browser checks; count commands are not covered`);
