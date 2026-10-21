import assert from "node:assert/strict";
import { test } from "node:test";
import { actionRequest, createV2Client, decimal, escapeHtml, V2Error, describeV2Error } from "../v2-client.js";
import { admin, manager, canOperate, roles } from "../v2-model.js";

test("v2 uses HttpOnly-cookie credentials and never a bearer token in browser JS", async () => {
  const calls = [];
  const client = createV2Client({ fetchImpl: async (path, options) => { calls.push({ path, options }); return Response.json({ id: "9007199254740993", quantity: "999999999999.999999" }); } });
  const data = await client.get("products", { search: "A&B", cursor: null });
  assert.equal(calls[0].path, "/api/v2/products?search=A%26B");
  assert.equal(calls[0].options.credentials, "same-origin");
  assert.equal(calls[0].options.headers.Authorization, undefined);
  assert.equal(data.id, "9007199254740993");
  assert.equal(data.quantity, "999999999999.999999");
});
test("frozen pending writes use the exact same key and body on retry", async () => {
  const source = { lines: [{ quantity: "0.123456" }] };
  const action = actionRequest("POST", "inventory/receipts", source);
  source.lines[0].quantity = "9";
  assert.equal(action.body.lines[0].quantity, "0.123456");
  assert.throws(() => { action.body.lines[0].quantity = "2"; }, TypeError);
  const calls = [];
  const client = createV2Client({ fetchImpl: async (_, options) => { calls.push(options); return calls.length === 1 ? Response.json({ code: "API_UNAVAILABLE" }, { status: 502 }) : Response.json({ number: "TX-1" }); } });
  await assert.rejects(client.send(action.method, action.path, action.body, action.key), error => error.retryable);
  await client.send(action.method, action.path, action.body, action.key);
  assert.equal(calls[0].headers["Idempotency-Key"], calls[1].headers["Idempotency-Key"]);
  assert.equal(calls[0].body, calls[1].body);
});
test("server validation errors remain final and carry field details", async () => {
  const client = createV2Client({ fetchImpl: async () => Response.json({ code: "VALIDATION_FAILED", errors: { unitId: ["Required"] } }, { status: 400 }) });
  await assert.rejects(client.send("POST", "products", {}), error => error instanceof V2Error && !error.retryable && error.errors.unitId[0] === "Required");
});

test("conflicts retain exact related IDs and report their bin labels as plain text", async () => {
  const subjects = [{ type: "bin", id: "9007199254740993", label: '<img src=x onerror="bad()">' }, null, { type: "bin", id: 3 }];
  const client = createV2Client({ fetchImpl: async () => Response.json({ code: "THRESHOLD_CONFLICT", title: "阈值冲突", subjects }, { status: 409 }) });
  await assert.rejects(client.send("PUT", "settings", {}), error => {
    assert.equal(error.retryable, false); assert.equal(error.subjects.length, 1); assert.equal(error.subjects[0].id, "9007199254740993");
    assert.ok(describeV2Error(error).includes("关联对象：库位 <img"));
    assert.ok(escapeHtml(describeV2Error(error)).includes("&lt;img")); return true;
  });
});

test("error summaries bound visible related objects without dropping their original evidence", () => {
  const error = new V2Error(409, { code: "ALREADY_EXISTS", subjects: Array.from({ length: 12 }, (_, i) => ({ type: "bin", id: String(i + 1), label: `A-R-01-${i + 1}` })) });
  assert.equal(error.subjects.length, 12); const text = describeV2Error(error);
  assert.ok(text.includes("共 12 项，仅显示前 10 项")); assert.ok(!text.includes("A-R-01-11"));
  assert.ok(describeV2Error(new Error("普通错误")).includes("普通错误"));
  assert.ok(describeV2Error(new V2Error(409, { code: "VERSION_CONFLICT", subjects: [{ type: "serializedItem", id: "9007199254740993" }] })).includes("实物 9007199254740993"));
});
test("a revoked session immediately ends the UI session without silent refresh", async () => {
  let ended = 0, requests = 0;
  const client = createV2Client({ sessionEnded: () => ended++, fetchImpl: async () => { requests++; return Response.json({ code: "SESSION_REVOKED" }, { status: 401 }); } });
  await assert.rejects(client.get("auth/me"));
  assert.equal(ended, 1); assert.equal(requests, 1);
});
test("responses belonging to an invalidated identity are discarded", async () => {
  let finish;
  const client = createV2Client({ fetchImpl: () => new Promise(resolve => finish = resolve) });
  const pending = client.get("products"); client.invalidate(); finish(Response.json({ items: [{ id: "1" }] }));
  await assert.rejects(pending, error => error.code === "SESSION_CHANGED");
});
test("an old restore request cannot revoke a newer identity", async () => {
  let finish, ended = 0;
  const client = createV2Client({ sessionEnded: () => ended++, fetchImpl: () => new Promise(resolve => finish = resolve) });
  const old = client.get("auth/me"); client.invalidate(); finish(Response.json({ code: "UNAUTHENTICATED" }, { status: 401 }));
  await assert.rejects(old, error => error.code === "SESSION_CHANGED"); assert.equal(ended, 0);
});
test("rate limits preserve Retry-After so UI can delay a same-key retry", async () => {
  const client = createV2Client({ fetchImpl: async () => Response.json({ code: "RATE_LIMITED" }, { status: 429, headers: { "Retry-After": "7" } }) });
  await assert.rejects(client.send("POST", "inventory/receipts", {}), error => error.retryable && error.retryAfterSeconds === 7);
});
test("network and invalid JSON responses are retryable but do not invent data", async () => {
  const offline = createV2Client({ fetchImpl: async () => { throw new Error("offline"); } });
  await assert.rejects(offline.get("products"), error => error.status === 0 && error.retryable);
  const invalid = createV2Client({ fetchImpl: async () => new Response("<html>proxy error</html>") });
  await assert.rejects(invalid.get("products"), error => error.status === 502 && error.retryable);
});
test("precise decimal validation never converts quantities to Number", () => {
  for (const x of ["999999999999.999999", "0.000001", "1.000000"]) assert.equal(decimal(x), x);
  assert.equal(decimal("-0.123456", 6, { signed: true }), "-0.123456");
  assert.equal(decimal("0", 0), "0");
  assert.equal(decimal("2.000000", 0), "2.000000");
  for (const x of ["1e3", "NaN", "Infinity", "1,000", "-1", "1.0000001", "1000000000000"]) assert.throws(() => decimal(x), x);
  assert.throws(() => decimal("0", 6, { positive: true }));
  assert.throws(() => decimal("-0", 6, { signed: true }));
  assert.throws(() => decimal("1.1", 0)); assert.throws(() => decimal("1", 7));
});
test("untrusted field content is escaped before HTML rendering", () => {
  assert.equal(escapeHtml('<img src=x onerror="alert(1)">'), "&lt;img src=x onerror=&quot;alert(1)&quot;&gt;");
  assert.equal(escapeHtml("O'Reilly & Co"), "O&#39;Reilly &amp; Co");
});
test("selected role, not union of assigned roles, controls visible actions", () => {
  const receiver = { roles: Object.keys(roles), currentRole: "receivingClerk" };
  assert.equal(admin(receiver), false); assert.equal(manager(receiver), false);
  assert.equal(canOperate(receiver, "receipts"), true); assert.equal(canOperate(receiver, "movements"), false);
  assert.equal(canOperate(receiver, "adjustments"), false);
  const shipper = { currentRole: "shippingClerk" };
  assert.equal(canOperate(shipper, "shipments"), true); assert.equal(canOperate(shipper, "receipts"), false);
  for (const type of ["receipts", "shipments", "movements", "adjustments"]) assert.ok(canOperate({ currentRole: "warehouseSupervisor" }, type));
});
