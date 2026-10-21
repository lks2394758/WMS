// Real B4 acceptance. Temporary warehouse, real API/BFF, existing isolated browser; no mock responses.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

let total = 0;
async function check(name, action) { await action(); total++; console.log("PASS " + name); }
await withRealV2(async ({ work, base, password, login, request }) => {
  const cookie = await login("supervisor");
  const get = async path => { const r = await request(path, { cookie }); assert.equal(r.status, 200); return r.json(); };
  const post = (path, body, key = randomUUID(), identity = cookie) => request(path, { method: "POST", body, key, cookie: identity });
  const ok = async (response, status = 200) => { assert.equal(response.status, status); return response.json(); };
  const problem = async (response, code, status = 409) => { assert.equal(response.status, status); assert.equal((await response.json()).code, code); };
  const unit = (await get("units")).items.find(x => x.name === "件");
  let cell = 0;
  async function stock(serialTracking = false) {
    const product = await ok(await post("products", { name: "盘点样品" + ++cell, unitId: unit.id, decimalPlaces: serialTracking ? 0 : 2, serialTracking, categoryIds: [] }), 201);
    const bin = await ok(await post("bins", { zone: "盘点区", rack: "A", level: "01", cell: String(cell).padStart(2, "0") }), 201);
    await ok(await post("inventory/receipts", { lines: [{ productId: product.id, toBinId: bin.id, capacity: "10", ...(serialTracking ? { serialNumbers: ["COUNT-SN-1", "COUNT-SN-2"] } : { quantity: "2.5" }) }] }), 201);
    return { product, bin };
  }
  const start = async bin => ok(await post("counts", { binId: bin.id }), 201);
  const command = (task, action, fields = {}, key) => post(`counts/${task.id}/${action}`, { version: task.version, round: task.round, ...(task.latestResultId ? { resultId: task.latestResultId } : {}), ...fields }, key);
  const normal = await stock();
  let task;
  await check("count start is blind, replay-safe and freezes inventory", async () => {
    const key = randomUUID(), body = { binId: normal.bin.id };
    task = await ok(await post("counts", body, key), 201);
    assert.equal(task.status, "inProgress"); assert.equal(task.results.length, 0); assert.ok(!("bookQuantity" in task.currentRound));
    const replay = await post("counts", body, key); assert.equal(replay.headers.get("idempotent-replayed"), "true"); assert.equal((await ok(replay, 201)).id, task.id);
    await problem(await post("inventory/adjustments", { lines: [{ productId: normal.product.id, toBinId: normal.bin.id, quantity: "1" }], reason: "冻结期间禁止调整" }), "BIN_FROZEN");
  });
  await check("cancelled recount restores pending result while keeping monotonic round and freeze", async () => {
    task = await ok(await command(task, "submit", { actualQuantity: "3" }));
    const resultId = task.latestResultId;
    task = await ok(await command(task, "recount", { reason: "复核" }));
    task = await ok(await command(task, "cancel", { reason: "采用上次结果" }));
    assert.equal(task.round, 2); assert.equal(task.status, "pendingConfirmation"); assert.equal(task.latestResultId, resultId);
    assert.equal((await get("bins/" + normal.bin.id)).frozenCountTaskId, task.id);
  });
  await check("confirmation uses restored latest result, adjusts exactly once and preserves immutable ledger", async () => {
    const original = task, key = randomUUID();
    task = await ok(await command(original, "confirm", { reason: "确认实盘" }, key));
    assert.equal(task.status, "completed"); assert.equal(task.results[0].disposition, "adjusted");
    const bin = await get("bins/" + normal.bin.id); assert.equal(bin.quantity, "3"); assert.equal(bin.frozenCountTaskId ?? null, null);
    const txId = task.results[0].adjustmentTransactionId; assert.ok(txId);
    const tx = await get("inventory/transactions/" + txId); assert.equal(tx.type, "adjustment"); assert.equal(tx.countResultId, task.latestResultId);
    const replay = await command(original, "confirm", { reason: "确认实盘" }, key); assert.equal(replay.headers.get("idempotent-replayed"), "true");
    assert.equal((await ok(replay)).results[0].adjustmentTransactionId, txId);
    assert.equal((await get("bins/" + normal.bin.id)).quantity, "3"); assert.deepEqual(await get("inventory/transactions/" + txId), tx);
  });
  const serial = await stock(true);
  let investigation;
  await check("serial discrepancy never edits stock or items and survives a zero-difference recount", async () => {
    const items = await get("serialized-items?binId=" + serial.bin.id);
    investigation = await start(serial.bin);
    investigation = await ok(await command(investigation, "submit", { actualQuantity: "1" }));
    investigation = await ok(await command(investigation, "confirm", { reason: "序列号差异待调查" }));
    assert.equal(investigation.hasUnresolvedInvestigation, true); assert.equal(investigation.results[0].disposition, "underInvestigation");
    assert.equal((await get("bins/" + serial.bin.id)).quantity, "2"); assert.deepEqual(await get("serialized-items?binId=" + serial.bin.id), items);
    investigation = await ok(await command(investigation, "recount", { reason: "重新核实" }));
    investigation = await ok(await command(investigation, "submit", { actualQuantity: "2" }));
    investigation = await ok(await command(investigation, "confirm"));
    assert.equal(investigation.hasUnresolvedInvestigation, true); assert.equal(investigation.results[0].disposition, "underInvestigation");
    assert.equal(investigation.results[1].disposition, "confirmedNoDifference"); assert.equal((await get("bins/" + serial.bin.id)).quantity, "2");
  });
  await check("resolve rejects an unrelated adjustment and current-role permissions are enforced", async () => {
    await problem(await command(investigation, "resolve", { conclusion: "错误关联", noAdjustmentNeeded: false, transactionId: task.results[0].adjustmentTransactionId }), "TRANSACTION_NOT_ELIGIBLE", 422);
    const receiver = await login("receiver");
    const response = await post(`counts/${investigation.id}/resolve`, { conclusion: "越权", noAdjustmentNeeded: true, version: investigation.version }, randomUUID(), receiver);
    assert.equal(response.status, 403); assert.equal((await response.json()).code, "FORBIDDEN");
    assert.equal((await get("counts/" + investigation.id)).hasUnresolvedInvestigation, true);
  });
  const browserStock = await stock();
  const browserTask = await start(browserStock.bin);
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    await cdp("Page.navigate", { url: base + "/v2/#counts" });
    await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password);
    await evaluate("document.querySelector('#login-form').requestSubmit()");
    await wait("!document.querySelector('#workspace').hidden && document.querySelector('#page').getAttribute('aria-busy')==='false'", "real count list");
    const detail = async id => { await click(`[data-action=detail][data-id="${id}"]`); await wait("document.querySelector('#editor').open && !!document.querySelector('[data-action^=count-]')", "count detail"); };
    const action = async (name, fields = {}) => {
      await click(`[data-action=count-${name}]`); await wait("document.querySelector('#editor').open && !document.querySelector('#editor-form button[type=submit]').hidden", "count editor");
      for (const [key, value] of Object.entries(fields)) {
        if (typeof value === "boolean") await evaluate(`document.querySelector('#editor-form [name=${key}]').checked=${value}`);
        else await field(`#editor-form [name=${key}]`, value);
      }
      await evaluate("document.querySelector('#editor-form').requestSubmit()");
      await wait("!document.querySelector('#editor').open && document.querySelector('#page').getAttribute('aria-busy')==='false'", "count saved");
    };
    await check("browser submits a blind round and confirms a real exact adjustment", async () => {
      await detail(browserTask.id); assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('本轮盲盘')"));
      await action("submit", { actualQuantity: "2.75", note: "页面实盘" });
      await detail(browserTask.id); assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('0.25')"));
      await action("confirm", { reason: "页面确认" });
      assert.equal((await get("bins/" + browserStock.bin.id)).quantity, "2.75"); assert.equal((await get("counts/" + browserTask.id)).status, "completed");
    });
    await check("browser resolves investigation explicitly and displays immutable resolution evidence", async () => {
      await detail(investigation.id); await action("resolve", { conclusion: "复盘证实数量正确，无需调整", noAdjustmentNeeded: true });
      const resolved = await get("counts/" + investigation.id); assert.equal(resolved.hasUnresolvedInvestigation, false);
      assert.equal(resolved.results[0].disposition, "resolved"); assert.ok(resolved.results[0].resolutionLogId);
      await click(`[data-action=detail][data-id="${investigation.id}"]`); await wait("document.querySelector('#editor').open && document.querySelector('#editor-fields').textContent.includes('结案记录')", "resolution evidence");
      assert.ok(await evaluate("!document.querySelector('[data-action=count-resolve]')"));
      await click('[data-close]');
    });
  });
});
console.log(`PASS ${total} REAL count/API/browser checks`);
