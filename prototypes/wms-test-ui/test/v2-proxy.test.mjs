import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { after, before, test } from "node:test";
import { createAppServer, loadConfig } from "../server.mjs";
import { allowsV2 } from "../v2-proxy.mjs";

let backend, ui, base; const seen = [], logs = [];
const fixtureCredential = "test_only_opaque_session";
async function listen(server) { server.listen(0, "127.0.0.1"); await once(server, "listening"); return `http://127.0.0.1:${server.address().port}`; }
before(async () => {
  backend = createServer((req, res) => {
    let body = ""; req.on("data", c => body += c); req.on("end", () => {
      seen.push({ path: req.url, headers: req.headers, body });
      res.setHeader("Content-Type", "application/json");
      if (req.url === "/api/v2/auth/login") res.end(JSON.stringify({ token: fixtureCredential, expiresAt: new Date(Date.now() + 3600000).toISOString(), user: { id: "1", currentRole: "systemAdministrator", roles: ["systemAdministrator"] } }));
      else if (req.url === "/api/v2/auth/me") { res.statusCode = 401; res.end('{"code":"SESSION_REVOKED"}'); }
      else res.end('{"items":[],"quantity":"0.123456"}');
    });
  });
  const url = await listen(backend);
  ui = createAppServer(loadConfig({ WMS_API_BASE_URL: url }), { logger: { log: s => logs.push(s) } }); base = await listen(ui);
});
after(() => { ui.closeAllConnections(); backend.closeAllConnections(); ui.close(); backend.close(); });
test("new UI and modules are available without exposing the BFF source", async () => {
  for (const path of ["/v2/", "/v2-app.js", "/v2-client.js", "/v2-model.js", "/v2.css"]) assert.equal((await fetch(base + path)).status, 200, path);
  assert.equal((await fetch(base + "/v2-proxy.mjs")).status, 404);
});
test("login cannot default to sending credentials in a URL before JavaScript initializes", async () => {
  const html = await (await fetch(base + "/v2/")).text();
  assert.match(html, /<form id="login-form"[^>]*method="post"/);
  assert.match(html, /<button type="submit" class="primary" disabled>登录/);
});
test("v2 keeps route allow-list narrow", () => {
  for (const [method, path] of [["GET", "/api/v2/products"], ["POST", "/api/v2/counts/1/recount"], ["PUT", "/api/v2/settings"]]) assert.ok(allowsV2(method, path));
  for (const path of ["/api/v2/admin", "/api/v2/products/not-a-number", "/api/v2/products/0", "/api/v2/../secrets"]) assert.equal(allowsV2("GET", path), false);
});

test("separate v2 backend never routes v2 requests to the legacy backend", async () => {
  const hits = [];
  const legacy = createServer((req, res) => { hits.push("v1:" + req.url); res.end("{}"); });
  const modern = createServer((req, res) => { hits.push("v2:" + req.url); res.end("{}"); });
  let proxy;
  try {
    const legacyBase = await listen(legacy), modernBase = await listen(modern);
    proxy = createAppServer(loadConfig({ WMS_API_BASE_URL: legacyBase, WMS_V2_API_BASE_URL: modernBase }), { logger: { log() {} } });
    const origin = await listen(proxy);
    assert.equal((await fetch(origin + "/api/v2/products")).status, 200);
    assert.equal((await fetch(origin + "/api/v1/products")).status, 200);
    assert.deepEqual(hits, ["v2:/api/v2/products", "v1:/api/v1/products"]);
    modern.closeAllConnections(); await new Promise(resolve => modern.close(resolve));
    assert.equal((await fetch(origin + "/api/v2/products")).status, 502);
    assert.deepEqual(hits, ["v2:/api/v2/products", "v1:/api/v1/products"]);
  } finally {
    for (const server of [proxy, legacy, modern]) { server?.closeAllConnections(); server?.close(); }
  }
});
test("login strips credential from JSON and puts it in a durable HttpOnly SameSite cookie", async () => {
  const response = await fetch(base + "/api/v2/auth/login", { method: "POST", headers: { "Content-Type": "application/json", Origin: base }, body: JSON.stringify({ username: "fixture", password: "not-a-real-password" }) });
  assert.equal(response.status, 200); const body = await response.text(); assert.ok(!body.includes(fixtureCredential));
  const cookie = response.headers.get("set-cookie"); assert.match(cookie, /HttpOnly/); assert.match(cookie, /SameSite=Strict/); assert.match(cookie, /Max-Age=3\d{3}/);
  assert.match(cookie, /Path=\/api\/v2/);
});
test("proxy forwards cookie credential as bearer, not arbitrary caller credentials or cookies", async () => {
  const response = await fetch(base + "/api/v2/products?search=private", { headers: { Cookie: `other=secret; wms_v2_session=${fixtureCredential}`, Authorization: "Bearer browser-supplied" } });
  assert.equal(response.status, 200); assert.equal((await response.json()).quantity, "0.123456");
  assert.equal(seen.at(-1).headers.authorization, `Bearer ${fixtureCredential}`); assert.equal(seen.at(-1).headers.cookie, undefined);
  assert.ok(!logs.join("\n").includes("private")); assert.ok(!logs.join("\n").includes(fixtureCredential));
});
test("expired and logged-out sessions clear cookie", async () => {
  const expired = await fetch(base + "/api/v2/auth/me"); assert.equal(expired.status, 401); assert.match(expired.headers.get("set-cookie"), /Max-Age=0/);
  const out = await fetch(base + "/api/v2/auth/logout", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }); assert.match(out.headers.get("set-cookie"), /Max-Age=0/);
});
test("cross-origin writes and non-JSON writes never reach backend", async () => {
  const before = seen.length;
  const cross = await fetch(base + "/api/v2/inventory/receipts", { method: "POST", headers: { Origin: "https://evil.example", "Content-Type": "application/json" }, body: "{}" });
  assert.equal(cross.status, 403);
  const noJson = await fetch(base + "/api/v2/inventory/receipts", { method: "POST", body: "{}" }); assert.equal(noJson.status, 415);
  assert.equal(seen.length, before);
});
test("oversized writes are rejected before any backend request", async () => {
  const count = seen.length;
  const response = await fetch(base + "/api/v2/products", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ imageBase64: "x".repeat(3 * 1024 * 1024) }) });
  assert.equal(response.status, 413); assert.equal(seen.length, count);
});
test("write methods preserve their body and persistent action key", async () => {
  const response = await fetch(base + "/api/v2/products/1", { method: "PUT", headers: { "Content-Type": "application/json", "Idempotency-Key": "fixture-key" }, body: '{"name":"test"}' });
  assert.equal(response.status, 200); assert.equal(seen.at(-1).headers["idempotency-key"], "fixture-key"); assert.equal(seen.at(-1).body, '{"name":"test"}');
});
