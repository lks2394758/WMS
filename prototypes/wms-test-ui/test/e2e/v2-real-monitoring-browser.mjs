// B5a: persisted monitoring events through the real API/BFF and existing browser. Temporary warehouse only.
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { withRealV2, withBrowser } from "./v2-real-harness.mjs";

let total = 0;
async function check(name, action) { await action(); total++; console.log("PASS " + name); }
await withRealV2(async ({ repo, work, base, password, request, login }) => {
  const cookie = await login("supervisor");
  const get = async (path, identity = cookie) => { const r = await request(path, { cookie: identity }); assert.equal(r.status, 200); return r.json(); };
  const post = async (path, body, status = 200) => { const r = await request(path, { method: "POST", body, cookie, key: randomUUID() }); assert.equal(r.status, status); return r.json(); };
  const unit = (await get("units")).items.find(x => x.name === "件");
  const product = await post("products", { name: "ÉCROU-历史一", unitId: unit.id, decimalPlaces: 2, serialTracking: false, categoryIds: [] }, 201);
  await post("products", { name: "écrou-历史二", unitId: unit.id, decimalPlaces: 0, serialTracking: false, categoryIds: [] }, 201);
  for (let i = 0; i < 11; i++) await post("products", { name: "零库存商品" + i, unitId: unit.id, decimalPlaces: 0, serialTracking: false, categoryIds: [] }, 201);
  const archived = await post("products", { name: "已归档样品", unitId: unit.id, decimalPlaces: 0, serialTracking: false, categoryIds: [] }, 201);
  assert.equal((await request("products/" + archived.id, { method: "DELETE", body: { version: archived.version }, cookie })).status, 204);
  const bin = await post("bins", { zone: "监控区", rack: "A", level: "01", cell: "01" }, 201);
  await post("inventory/receipts", { lines: [{ productId: product.id, toBinId: bin.id, quantity: "1", capacity: "10" }] }, 201);
  const serial = await post("products", { name: "待调查设备", unitId: unit.id, decimalPlaces: 0, serialTracking: true, categoryIds: [] }, 201);
  const serialBin = await post("bins", { zone: "监控区", rack: "A", level: "01", cell: "02" }, 201);
  await post("inventory/receipts", { lines: [{ productId: serial.id, toBinId: serialBin.id, capacity: "10", serialNumbers: ["MON-1", "MON-2"] }] }, 201);
  let investigation = await post("counts", { binId: serialBin.id }, 201);
  investigation = await post(`counts/${investigation.id}/submit`, { round: investigation.round, version: investigation.version, actualQuantity: "1" });
  investigation = await post(`counts/${investigation.id}/confirm`, { round: investigation.round, version: investigation.version, resultId: investigation.latestResultId });
  const active = await post("counts", { binId: bin.id }, 201);
  assert.equal((await request("counts", { method: "POST", body: { binId: bin.id }, cookie, key: randomUUID() })).status, 409);
  const me = await get("auth/me");
  let low;
  await check("dashboard counts persisted states, includes zero-stock SKUs and caps both evidence lists", async () => {
    const all = await get("dashboard"), mine = await get("dashboard?scope=mine");
    assert.equal(all.skuCount, (await get("products?limit=200")).items.length); assert.equal(all.skuCount, 14);
    assert.equal(all.activeCountCount, 1); assert.equal(all.unresolvedInvestigationCount, 1); assert.equal(all.openAlertCount, 2);
    assert.equal(all.recentActivity.length, 10); assert.equal(all.alerts.length, 2); assert.ok(all.alerts.every(x => x.status === "open"));
    assert.equal(mine.scope, "mine"); assert.ok(mine.recentActivity.every(x => x.actorId === me.user.id));
    for (const key of ["skuCount", "openAlertCount", "activeCountCount", "unresolvedInvestigationCount"]) assert.equal(all[key], mine[key]);
  });
  await check("alert filters return immutable trigger snapshots and precise string quantities", async () => {
    const alerts = await get(`alerts?status=open&type=lowStock&binId=${bin.id}&productId=${product.id}`);
    low = assertOne(alerts.items); assert.equal(low.trigger.quantity, "1"); assert.equal(low.trigger.capacity, "10"); assert.equal(low.trigger.occupancyPercent, "10");
    const countAlert = assertOne((await get("alerts?type=countDiscrepancy")).items);
    assert.equal(countAlert.countTaskId, investigation.id); assert.equal(countAlert.countResultId, investigation.latestResultId);
    assert.equal(countAlert.trigger.bookQuantity, "2"); assert.equal(countAlert.trigger.actualQuantity, "1");
    const first = await get("alerts?limit=1"); assert.ok(first.nextCursor);
    const second = await get("alerts?limit=1&cursor=" + encodeURIComponent(first.nextCursor)); assert.notEqual(first.items[0].id, second.items[0].id);
  });
  await check("activity query searches Unicode before paging and filters structured failure/subject evidence", async () => {
    const prefix = "activity-logs?type=productCreate&search=" + encodeURIComponent("ÉCROU") + "&limit=1";
    const first = await get(prefix); assert.equal(first.items.length, 1); assert.ok(first.nextCursor);
    const second = await get(prefix + "&cursor=" + encodeURIComponent(first.nextCursor)); assert.equal(second.items.length, 1); assert.notEqual(first.items[0].id, second.items[0].id);
    const logs = await get(`activity-logs?productId=${product.id}&binId=${bin.id}&type=receipt&success=true&actorId=${me.user.id}`);
    const receipt = assertOne(logs.items); assert.equal(receipt.actorRole, "warehouseSupervisor"); assert.ok(receipt.subjects.some(x => x.type === "product" && x.id === product.id));
    const instant = encodeURIComponent(receipt.occurredAt);
    assert.equal(assertOne((await get(`activity-logs?type=receipt&binId=${bin.id}&from=${instant}`)).items).id, receipt.id);
    assert.equal((await get(`activity-logs?type=receipt&binId=${bin.id}&to=${instant}`)).items.length, 0);
    const failure = assertOne((await get(`activity-logs?type=countStart&binId=${bin.id}&success=false`)).items); assert.equal(failure.failureCode, "COUNT_TASK_EXISTS");
    const text = JSON.stringify(await get("activity-logs?limit=200")); assert.ok(!text.includes(password)); assert.ok(!text.includes("passwordHash"));
  });
  await check("all roles may read monitoring but anonymous and invalid filters are rejected", async () => {
    const receiver = await login("receiver");
    for (const path of ["dashboard", "alerts", "activity-logs"]) { assert.equal((await request(path)).status, 401); await get(path, receiver); }
    for (const path of ["dashboard?scope=invalid", "alerts?type=invalid", "alerts?binId=1.5", "activity-logs?type=invalid", "activity-logs?from=invalid", "activity-logs?from=2026-10-20T01%3A00%3A00Z&to=2026-10-19T01%3A00%3A00Z"]) assert.equal((await request(path, { cookie })).status, 400, path);
  });
  await check("continued alerts keep the original snapshot and transitions preserve resolved evidence", async () => {
    await post(`counts/${active.id}/cancel`, { version: active.version, round: active.round, reason: "完成监控测试" });
    await post("inventory/adjustments", { lines: [{ productId: product.id, toBinId: bin.id, quantity: "-0.5" }], reason: "持续低位" }, 201);
    assert.deepEqual(assertOne((await get(`alerts?status=open&binId=${bin.id}`)).items).trigger, low.trigger);
    await post("inventory/adjustments", { lines: [{ productId: product.id, toBinId: bin.id, quantity: "8.5" }], reason: "恢复并进入高位" }, 201);
    const resolved = assertOne((await get(`alerts?status=resolved&type=lowStock&binId=${bin.id}`)).items);
    assert.equal(resolved.id, low.id); assert.deepEqual(resolved.trigger, low.trigger); assert.ok(resolved.resolvedAt);
    assert.equal(assertOne((await get(`alerts?status=open&binId=${bin.id}`)).items).type, "highStock");
  });
  await withBrowser(work, async ({ cdp, evaluate, wait, click, field }) => {
    await cdp("Emulation.setDeviceMetricsOverride", { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await cdp("Page.navigate", { url: base + "/v2/#dashboard" }); await wait("document.querySelector('#login-form')?.dataset.ready==='true'", "initialized login");
    await field('#login-form [name=username]', "admin"); await field('#login-form [name=password]', password); await evaluate("document.querySelector('#login-form').requestSubmit()");
    const ready = "document.querySelector('#page').getAttribute('aria-busy')==='false'";
    await wait("!document.querySelector('#workspace').hidden && " + ready, "real dashboard");
    await check("real dashboard shows investigation count, role evidence, own scope and quick product search", async () => {
      assert.equal(await evaluate("document.querySelectorAll('.stat').length"), 4);
      assert.ok(await evaluate("document.querySelector('#page').textContent.includes('待调查盘点') && document.querySelector('#page').textContent.includes('仓库主管')"));
      await field("#activity-scope", "mine"); await wait(ready + " && document.querySelector('#activity-scope')?.value==='mine'", "own activity");
      assert.ok(!await evaluate("document.querySelector('#page').textContent.includes('仓库主管')"));
      await field("#quick-query", "ÉCROU"); await evaluate("document.querySelector('#quick-search').requestSubmit()");
      await wait("location.hash==='#products' && " + ready + " && document.querySelector('#page').textContent.includes('ÉCROU-历史一')", "product results");
    });
    await check("browser filters resolved alerts, retains conditions and opens immutable trigger evidence", async () => {
      await evaluate("location.hash='alerts'"); await wait(ready + " && !!document.querySelector('#filter-form [name=status]')", "alert list");
      await field('#filter-form [name=status]', "resolved"); await field('#filter-form [name=type]', "lowStock"); await field('#filter-form [name=binId]', bin.id);
      await evaluate("document.querySelector('#filter-form').requestSubmit()"); await wait(ready + " && !!document.querySelector('[data-action=alert-detail]') && document.querySelector('#filter-form [name=status]').value==='resolved'", "resolved filter");
      assert.equal(await evaluate("document.querySelector('#filter-form [name=type]').value"), "lowStock");
      await click(`[data-action=alert-detail][data-id="${low.id}"]`); await wait("document.querySelector('#editor').open", "alert snapshot");
      assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('触发时快照（不是当前库存）') && document.querySelector('#editor-fields').textContent.includes('已解决')"));
      await click('[data-close]');
    });
    await check("browser filters failed operations and shows who, role, time, failure and related objects", async () => {
      await evaluate("location.hash='activity-logs'"); await wait(ready + " && !!document.querySelector('#filter-form [name=success]')", "activity list");
      await field('#filter-form [name=success]', "false"); await field('#filter-form [name=type]', "countStart"); await field('#filter-form [name=binId]', bin.id);
      await evaluate("document.querySelector('#filter-form').requestSubmit()"); await wait(ready + " && !!document.querySelector('[data-action=log-detail]') && document.querySelector('#filter-form [name=success]').value==='false'", "failed operations");
      await click('[data-action=log-detail]'); await wait("document.querySelector('#editor').open", "failure evidence");
      assert.ok(await evaluate("document.querySelector('#editor-fields').textContent.includes('COUNT_TASK_EXISTS') && document.querySelector('#editor-fields').textContent.includes('仓库主管')"));
      const directory = join(repo, "artifacts", "v2-real-ui"); mkdirSync(directory, { recursive: true });
      const { data } = await cdp("Page.captureScreenshot", { format: "png" }); writeFileSync(join(directory, "monitoring.png"), Buffer.from(data, "base64"));
      await click('[data-close]');
    });
  });
});
function assertOne(items) { assert.equal(items.length, 1); return items[0]; }
console.log(`PASS ${total} REAL monitoring/API/browser checks`);
