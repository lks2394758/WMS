// Real inventory acceptance. Requires B3a implementation and a freshly built API; never uses demonstration databases.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

let total = 0;
const micros = value => { const [whole, fraction = ""] = value.split("."); return BigInt(whole) * 1000000n + BigInt(fraction.padEnd(6, "0")); };
async function check(name, action) { await action(); total++; console.log("PASS " + name); }
await withRealV2(async ({ repo, work, base, password, login, request }) => {
  const supervisor = await login("supervisor"), receiver = await login("receiver"), shipper = await login("shipper");
  const get = async path => { const response = await request(path, { cookie: supervisor }); assert.equal(response.status, 200); return response.json(); };
  const write = (path, body, { key = randomUUID(), cookie = supervisor, method = "POST" } = {}) => request(path, { body, key, cookie, method });
  const created = async (path, body, options) => { const response = await write(path, body, options); assert.equal(response.status, 201); return response.json(); };
  const problem = async (response, status, code) => { assert.equal(response.status, status); assert.equal((await response.json()).code, code); };
  const units = await get("units"), piece = units.items.find(x => x.name === "件"); assert.ok(piece);
  const bins = [];
  for (const cell of ["01", "02", "03", "04", "05", "06"]) bins.push(await created("bins", { zone: "测试区", rack: "R", level: "01", cell }));
  let product, originalReceipt, replayBody, replayKey;
  await check("receiving clerk creates an inline fractional-unit product with exact packaging conversion", async () => {
    originalReceipt = await created("inventory/receipts", { source: "真实联调供应商", lines: [{ toBinId: bins[0].id, capacity: "2.5", packagingUnit: "包", packagingQuantity: "5", conversionFactor: "0.25", newProduct: { name: "联调铜材", decimalPlaces: 3, serialTracking: false, newUnitName: "千克" } }] }, { cookie: receiver });
    const line = originalReceipt.lines[0]; assert.equal(line.quantity, "1.25"); assert.equal(line.toBefore, "0"); assert.equal(line.toAfter, "1.25"); assert.equal(line.unitName, "千克");
    assert.equal(originalReceipt.actorRole, "receivingClerk"); product = await get("products/" + line.productId);
    assert.equal(product.totalQuantity, "1.25"); assert.equal(product.decimalPlaces, 3); assert.equal((await get("bins/" + bins[0].id)).occupancyPercent, "50");
  });
  await check("shipment, movement and signed adjustment commit exact before/after quantities", async () => {
    const shipped = await created("inventory/shipments", { destination: "展示客户", lines: [{ productId: product.id, fromBinId: bins[0].id, quantity: "0.3" }] }, { cookie: shipper });
    assert.equal(shipped.lines[0].fromAfter, "0.95");
    const moved = await created("inventory/movements", { lines: [{ productId: product.id, fromBinId: bins[0].id, toBinId: bins[1].id, quantity: "0.2", capacity: "1", lowThreshold: "0", highThreshold: "100" }] });
    assert.equal(moved.lines[0].fromAfter, "0.75"); assert.equal(moved.lines[0].toAfter, "0.2");
    const adjusted = await created("inventory/adjustments", { reason: "按实际数量纠正", lines: [{ productId: product.id, toBinId: bins[0].id, quantity: "-0.05" }] });
    assert.equal(adjusted.lines[0].toAfter, "0.7"); assert.equal((await get("products/" + product.id)).totalQuantity, "0.9");
  });
  await check("over-capacity is accepted and restocking an emptied bin resets capacity and overrides", async () => {
    await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: bins[1].id, quantity: "0.9" }] });
    assert.equal((await get("bins/" + bins[1].id)).capacityStatus, "overCapacity");
    await created("inventory/shipments", { lines: [{ productId: product.id, fromBinId: bins[1].id, quantity: "1.1" }] });
    const retained = await get("bins/" + bins[1].id); assert.equal(retained.quantity, "0"); assert.equal(retained.productId, product.id);
    await problem(await write("inventory/receipts", { lines: [{ productId: product.id, toBinId: bins[1].id, quantity: "0.3" }] }), 422, "CAPACITY_REQUIRED");
    await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: bins[1].id, quantity: "0.3", capacity: "3" }] });
    const reset = await get("bins/" + bins[1].id); assert.equal(reset.capacity, "3"); assert.equal(reset.effectiveLowThreshold, "20"); assert.equal(reset.effectiveHighThreshold, "80"); assert.equal(reset.capacityStatus, "low");
    assert.ok(reset.lowThreshold == null && reset.highThreshold == null);
  });
  await check("a later invalid line rolls back earlier stock and ledger changes", async () => {
    const before = await get("bins/" + bins[0].id), transactions = await get("inventory/transactions");
    await problem(await write("inventory/shipments", { lines: [{ productId: product.id, fromBinId: bins[0].id, quantity: "0.1" }, { productId: product.id, fromBinId: bins[0].id, quantity: "999" }] }), 409, "INSUFFICIENT_STOCK");
    assert.deepEqual(await get("bins/" + bins[0].id), before); assert.deepEqual(await get("inventory/transactions"), transactions);
  });
  await check("inline unit and product creation are rolled back with a failed receipt", async () => {
    await problem(await write("inventory/receipts", { lines: [{ toBinId: bins[2].id, capacity: "10", quantity: "1", newProduct: { name: "不应保留的商品", newUnitName: "不应保留的单位", decimalPlaces: 0, serialTracking: false } }, { productId: product.id, toBinId: bins[0].id, quantity: "1", capacity: "8" }] }, { cookie: receiver }), 422, "CAPACITY_NOT_ALLOWED");
    assert.equal((await get("products?search=" + encodeURIComponent("不应保留的商品"))).items.length, 0);
    assert.equal((await get("units?search=" + encodeURIComponent("不应保留的单位"))).items.length, 0);
    assert.equal((await get("bins/" + bins[2].id)).quantity, "0");
  });
  await check("precision, packaging mismatch and quantity overflow are rejected without rounding", async () => {
    const line = { productId: product.id, toBinId: bins[0].id };
    await problem(await write("inventory/receipts", { lines: [{ ...line, quantity: "0.0001" }] }), 422, "PRECISION_EXCEEDED");
    const mismatch = await write("inventory/receipts", { lines: [{ ...line, quantity: "1", packagingUnit: "包", packagingQuantity: "2", conversionFactor: "0.25" }] }); assert.ok([400, 422].includes(mismatch.status));
    await problem(await write("inventory/receipts", { lines: [{ ...line, packagingUnit: "包", packagingQuantity: "0.000001", conversionFactor: "0.000001" }] }), 422, "PRECISION_EXCEEDED");
    await problem(await write("inventory/receipts", { lines: [{ ...line, packagingUnit: "包", packagingQuantity: "999999999999", conversionFactor: "999999999999" }] }), 422, "QUANTITY_OUT_OF_RANGE");
  });
  await check("same-key retries and concurrent repeats commit one immutable transaction", async () => {
    replayBody = { source: "同请求重试", lines: [{ productId: product.id, toBinId: bins[0].id, quantity: "0.123" }] }; replayKey = randomUUID();
    const before = await get("bins/" + bins[0].id);
    const responses = await Promise.all(Array.from({ length: 3 }, () => write("inventory/receipts", replayBody, { key: replayKey })));
    assert.ok(responses.every(x => x.status === 201)); const bodies = await Promise.all(responses.map(x => x.json())); assert.equal(new Set(bodies.map(x => x.id)).size, 1);
    assert.ok(responses.filter(x => x.headers.get("idempotent-replayed") === "true").length >= 2);
    assert.equal(micros((await get("bins/" + bins[0].id)).quantity) - micros(before.quantity), 123000n);
    await problem(await write("inventory/receipts", { ...replayBody, source: "不同正文" }, { key: replayKey }), 409, "IDEMPOTENCY_CONFLICT");
    await problem(await request("inventory/receipts", { cookie: supervisor, method: "POST", body: replayBody }), 400, "VALIDATION_FAILED");
  });
  await check("stocked products keep immutable receipt evidence after a name correction", async () => {
    const current = await get("products/" + product.id);
    const changed = await write("products/" + product.id, { name: "联调铜材（更名）", unitId: current.unitId, decimalPlaces: current.decimalPlaces, serialTracking: false, categoryIds: current.categoryIds, version: current.version }, { method: "PUT" }); assert.equal(changed.status, 200);
    assert.deepEqual(await get("inventory/transactions/" + originalReceipt.id), originalReceipt);
    const replay = await write("inventory/receipts", replayBody, { key: replayKey }); assert.equal(replay.status, 201); assert.equal((await replay.json()).lines[0].productName, "联调铜材");
  });
  await check("receipt and movement permissions use only the current role", async () => {
    await problem(await write("inventory/receipts", replayBody, { cookie: shipper }), 403, "FORBIDDEN");
    await problem(await write("inventory/movements", { lines: [{ productId: product.id, fromBinId: bins[0].id, toBinId: bins[1].id, quantity: "0.1" }] }, { cookie: receiver }), 403, "FORBIDDEN");
  });
  await check("concurrent shipments cannot both spend the same committed stock", async () => {
    await created("inventory/receipts", { lines: [{ productId: product.id, toBinId: bins[5].id, capacity: "1", quantity: "0.3" }] });
    const body = { lines: [{ productId: product.id, fromBinId: bins[5].id, quantity: "0.2" }] };
    const results = await Promise.all([write("inventory/shipments", body), write("inventory/shipments", body)]);
    assert.equal(results.filter(x => x.status === 201).length, 1); await problem(results.find(x => x.status !== 201), 409, "INSUFFICIENT_STOCK");
    assert.equal((await get("bins/" + bins[5].id)).quantity, "0.1"); assert.equal((await get("inventory/transactions?binId=" + bins[5].id)).items.length, 2);
  });
  await check("Unicode ledger search filters before cursor pagination and retains exact JSON types", async () => {
    const ids = [];
    for (const source of ["écrou alpha", "ÉCROU beta"]) ids.push((await created("inventory/receipts", { source, lines: [{ productId: product.id, toBinId: bins[0].id, quantity: "0.001" }] })).id);
    const query = "inventory/transactions?limit=1&search=" + encodeURIComponent("ÉCROU");
    const first = await get(query); assert.equal(first.items.length, 1); assert.ok(first.nextCursor);
    const second = await get(query + "&cursor=" + encodeURIComponent(first.nextCursor)); assert.equal(second.items.length, 1); assert.ok(!second.nextCursor);
    assert.deepEqual(new Set([first.items[0].id, second.items[0].id]), new Set(ids));
    assert.equal((await write("inventory/receipts", { lines: [{ productId: Number(product.id), toBinId: bins[0].id, quantity: "1" }] })).status, 400);
    assert.equal((await write("inventory/receipts", { lines: [{ productId: product.id, toBinId: bins[0].id, quantity: 1 }] })).status, 400);
  });
  await check("serialized receipt, movement, shipment and return retain physical item IDs", async () => {
    const serial = await created("products", { name: "逐件测试设备", unitId: piece.id, decimalPlaces: 0, serialTracking: true, categoryIds: [] });
    const receipt = await created("inventory/receipts", { lines: [{ productId: serial.id, toBinId: bins[3].id, capacity: "10", serialNumbers: ["E2E-α", "E2E-B"] }] });
    const ids = receipt.lines[0].items; assert.equal(ids.length, 2); assert.equal(receipt.lines[0].quantity, "2");
    const moved = await created("inventory/movements", { lines: [{ productId: serial.id, fromBinId: bins[3].id, toBinId: bins[4].id, capacity: "10", serialNumbers: ["E2E-α"] }] });
    assert.equal(moved.lines[0].items[0].serializedItemId, ids[0].serializedItemId);
    await created("inventory/shipments", { lines: [{ productId: serial.id, fromBinId: bins[4].id, serialNumbers: ["E2E-α"] }] });
    const returned = await created("inventory/receipts", { lines: [{ productId: serial.id, toBinId: bins[4].id, capacity: "8", serialNumbers: ["E2E-α"] }] }); assert.equal(returned.lines[0].items[0].serializedItemId, ids[0].serializedItemId);
    await problem(await write("inventory/receipts", { lines: [{ productId: serial.id, toBinId: bins[4].id, serialNumbers: ["e2e-α"] }] }), 409, "SERIAL_ALREADY_IN_STOCK");
    await problem(await write("inventory/shipments", { lines: [{ productId: serial.id, fromBinId: bins[4].id, serialNumbers: ["E2E-B"] }] }), 409, "SERIAL_NOT_IN_BIN");
  });
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    const navigate = async (page, title) => { await evaluate(`location.hash=${JSON.stringify(page)}`); await wait(`document.querySelector('#page-title').textContent===${JSON.stringify(title)} && document.querySelector('#page').getAttribute('aria-busy')==='false'`, title); };
    await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await cdp("Page.navigate", { url: base + "/v2/#operations" }); await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password); await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && !!document.querySelector('#operation-form') && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real operation form");
    await check("real browser submits exact fractional receipt and reloads actual backend stock", async () => {
      const before = await get("bins/" + bins[0].id);
      await field('#operation-form .operation-line [name=productId]', product.id); await field('#operation-form .operation-line [name=toBinId]', bins[0].id); await field('#operation-form .operation-line [name=quantity]', "0.125");
      await evaluate("document.querySelector('#operation-form').dataset.beforeSave='true';document.querySelector('#operation-form').requestSubmit()");
      await wait("!!document.querySelector('#operation-form') && !document.querySelector('#operation-form').dataset.beforeSave && document.querySelector('#notice').textContent.includes('操作已提交')", "real receipt submitted");
      assert.equal(micros((await get("bins/" + bins[0].id)).quantity) - micros(before.quantity), 125000n);
      await navigate("inventory/transactions", "库存流水"); assert.ok(await evaluate("document.querySelector('#page').textContent.includes('入库')"));
      const latest = (await get("inventory/transactions?type=receipt")).items[0]; await click(`[data-action=detail][data-id="${latest.id}"]`);
      await wait("document.querySelector('#editor').open && document.querySelector('#editor-fields').textContent.includes('0.125')", "immutable real receipt detail");
      assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('管理员') && !document.querySelector('#editor-fields').textContent.includes('systemAdministrator')"));
      const directory = join(repo, "artifacts", "v2-real-ui"); mkdirSync(directory, { recursive: true }); const { data } = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(directory, "inventory.png"), Buffer.from(data, "base64"));
    });
    await check("real browser preserves shipment mode after a successful write and stock reload", async () => {
      await click('[data-close]'); await wait("!document.querySelector('#editor').open", "close ledger detail");
      await navigate("operations", "出入库操作"); await click('[data-action=operation][data-id=shipments]');
      const before = await get("bins/" + bins[0].id);
      await field('#operation-form [name=productId]', product.id); await field('#operation-form [name=fromBinId]', bins[0].id); await field('#operation-form [name=quantity]', "0.125");
      await evaluate("document.querySelector('#operation-form').dataset.beforeSave='true';document.querySelector('#operation-form').requestSubmit()");
      await wait("!!document.querySelector('#operation-form') && !document.querySelector('#operation-form').dataset.beforeSave && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real shipment saved");
      assert.equal(await evaluate("document.querySelector('.tabs .selected').dataset.id"), "shipments");
      assert.equal(micros(before.quantity) - micros((await get("bins/" + bins[0].id)).quantity), 125000n);
    });
    await check("real browser keeps a rejected shipment editable without inventing a stock change", async () => {
      const before = await get("bins/" + bins[0].id);
      await field('#operation-form [name=productId]', product.id); await field('#operation-form [name=fromBinId]', bins[0].id); await field('#operation-form [name=quantity]', "999");
      await evaluate("document.querySelector('#operation-form').requestSubmit()");
      await wait("document.querySelector('#notice').textContent.includes('INSUFFICIENT_STOCK')", "real insufficient-stock refusal");
      assert.equal(await evaluate("document.querySelector('#operation-form [name=quantity]').value"), "999");
      assert.equal(await evaluate("document.querySelector('#pending').hidden"), true);
      assert.deepEqual(await get("bins/" + bins[0].id), before);
    });
  });
});
console.log(`PASS ${total} REAL inventory/API/browser checks; count commands and serial correction need separate acceptance`);
