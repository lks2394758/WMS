// Real Kestrel audit checks, not an in-memory server. Credentials and all warehouse data are temporary.
import assert from "node:assert/strict";
import { request as httpRequest } from "node:http";
import { randomUUID } from "node:crypto";
import { withRealV2 } from "../prototypes/wms-test-ui/test/e2e/v2-real-harness.mjs";

let total = 0;
async function check(name, work) { await work(); total++; console.log("PASS " + name); }
await withRealV2(async ({ upstream, password, request, login }) => {
  const cookie = await login("supervisor");
  const marker = "BODY-MUST-NOT-BE-AUDITED-" + randomUUID();
  const logs = async type => {
    const response = await request(`activity-logs?success=false&type=${type}&limit=200`, { cookie });
    assert.equal(response.status, 200); return (await response.json()).items;
  };
  const rawLogin = async username => {
    const response = await fetch(upstream + "/api/v2/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username, password }) });
    assert.equal(response.status, 200); return (await response.json()).token;
  };
  let token = await rawLogin("admin");
  const send = (path, body, options = {}) => fetch(upstream + "/api/v2/" + path, { method: "POST", signal: AbortSignal.timeout(20000), headers: { "Content-Type": "application/json", "Idempotency-Key": randomUUID(), ...(options.token === null ? {} : { Authorization: "Bearer " + (options.token || token) }), ...options.headers }, body });
  async function exactlyOne(type, code, status, operation, inspect = () => {}) {
    const before = new Set((await logs(type)).map(x => x.id));
    const response = await operation(); assert.equal(response.status, status);
    // Audit runs before request completion; tolerate only a short publication delay, never invent evidence.
    let added = [];
    for (let i = 0; i < 20; i++) {
      added = (await logs(type)).filter(x => !before.has(x.id));
      if (added.length) break; await new Promise(resolve => setTimeout(resolve, 50));
    }
    assert.equal(added.length, 1); assert.equal(added[0].failureCode, code);
    const text = JSON.stringify(added); assert.ok(!text.includes(marker)); assert.ok(!text.includes(password)); assert.ok(!text.includes(token));
    inspect(added[0]); return response;
  }
  await check("malformed JSON is rejected and audited once without copying request data", async () => {
    await exactlyOne("productCreate", "VALIDATION_FAILED", 400, () => send("products", '{"name":"' + marker + '","unitId":'));
  });
  await check("model binding and unsupported media type each leave one safe failure", async () => {
    await exactlyOne("productCreate", "VALIDATION_FAILED", 400, () => send("products", JSON.stringify({ name: marker, unitId: {}, decimalPlaces: 0, serialTracking: false, categoryIds: [] })));
    await exactlyOne("productCreate", "VALIDATION_FAILED", 415, () => send("products", marker, { headers: { "Content-Type": "text/plain" } }));
  });
  await check("missing and revoked credentials never attribute a trusted role to a failed command", async () => {
    await exactlyOne("productCreate", "UNAUTHENTICATED", 401, () => send("products", "{}", { token: null }), log => { assert.equal(log.actorId ?? null, null); assert.equal(log.actorRole ?? null, null); });
    const old = token; token = await rawLogin("admin");
    await exactlyOne("productCreate", "SESSION_REVOKED", 401, () => send("products", "{}", { token: old }), log => assert.equal(log.actorRole ?? null, null));
  });
  await check("current-role policy rejection is audited once", async () => {
    const receiver = await rawLogin("receiver");
    await exactlyOne("productCreate", "FORBIDDEN", 403, () => send("products", "{}", { token: receiver }), log => assert.equal(log.actorRole, "receivingClerk"));
  });
  await check("declared and chunked oversized bodies on real Kestrel are audited once", async () => {
    await exactlyOne("productCreate", "PAYLOAD_TOO_LARGE", 413, () => send("products", JSON.stringify({ name: marker, blob: "x".repeat(5 * 1024 * 1024) })));
    await exactlyOne("productCreate", "PAYLOAD_TOO_LARGE", 413, () => chunkedOversize(upstream, token, marker));
  });
  await check("ordinary business and login failures are not duplicated by framework auditing", async () => {
    await exactlyOne("productCreate", "VALIDATION_FAILED", 400, () => send("products", JSON.stringify({ name: "", unitId: "1", decimalPlaces: 0, serialTracking: false, categoryIds: [] })));
    const before = new Set((await logs("login")).map(x => x.id));
    const response = await fetch(upstream + "/api/v2/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: "missing-account", password: marker }) });
    assert.equal(response.status, 401); const added = (await logs("login")).filter(x => !before.has(x.id)); assert.equal(added.length, 1);
    assert.ok(!JSON.stringify(added).includes(marker));
  });
  await check("rate-limited mutation keeps Retry-After and creates exactly one audit record", async () => {
    const shipper = await rawLogin("shipper"); let limited = false;
    for (let i = 0; i < 130; i++) {
      const response = await fetch(upstream + "/api/v2/auth/me", { headers: { Authorization: "Bearer " + shipper }, signal: AbortSignal.timeout(10000) });
      await response.arrayBuffer(); if (response.status === 429) { limited = true; break; } assert.equal(response.status, 200);
    }
    assert.ok(limited);
    const response = await exactlyOne("shipment", "RATE_LIMITED", 429, () => send("inventory/shipments", "{}", { token: shipper }));
    assert.ok(Number(response.headers.get("retry-after")) > 0);
  });
});
function chunkedOversize(upstream, token, marker) {
  return new Promise((resolve, reject) => {
    const req = httpRequest(upstream + "/api/v2/products", { method: "POST", headers: { "Content-Type": "application/json", "Transfer-Encoding": "chunked", Authorization: "Bearer " + token } }, res => {
      res.resume(); res.on("end", () => resolve({ status: res.statusCode })); res.on("error", reject);
    });
    req.on("error", () => reject(new Error("Oversize probe transport failure")));
    req.setTimeout(15000, () => req.destroy(new Error("Oversize probe timeout")));
    req.write('{"name":"' + marker + '","blob":"'); req.write("x".repeat(5 * 1024 * 1024)); req.end('"}');
  });
}
console.log(`PASS ${total} REAL framework-audit checks`);
