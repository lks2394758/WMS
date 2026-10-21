import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { after, before, describe, test } from "node:test";
import { createAppServer, loadConfig } from "../server.mjs";

// A fake WMS API that records what reaches it, so the tests can check what the proxy forwards.
const seen = [];
let fakeApi;
let ui;
let uiBase;
const logs = [];

async function listen(server) {
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return `http://127.0.0.1:${server.address().port}`;
}

before(async () => {
  fakeApi = createServer((request, response) => {
    let body = "";
    request.on("data", chunk => { body += chunk; });
    request.on("end", () => {
      seen.push({ method: request.method, url: request.url, headers: request.headers, body });
      if (request.url.startsWith("/api/v1/products")) {
        response.writeHead(200, { "Content-Type": "application/json", "X-Correlation-ID": "abc", "Set-Cookie": "x=1", "X-Internal": "secret" });
        response.write('{"items":[');
        setTimeout(() => response.end('],"nextCursor":null}'), 20); // streamed in two chunks
      } else if (request.url === "/api/v1/inventory/receipts") {
        response.writeHead(429, { "Content-Type": "application/problem+json", "Retry-After": "7", "Idempotent-Replayed": "true" });
        response.end('{"code":"RATE_LIMITED","status":429}');
      } else if (request.url === "/api/v1/inventory/shipments") {
        setTimeout(() => { if (!response.destroyed) response.end("{}"); }, 2000); // slower than the proxy timeout
      } else {
        response.writeHead(401, { "WWW-Authenticate": "Bearer" });
        response.end();
      }
    });
  });
  const apiBase = await listen(fakeApi);
  ui = createAppServer({ ...loadConfig({ WMS_API_BASE_URL: apiBase }), timeoutMs: 300 }, { logger: { log: line => logs.push(line) } });
  uiBase = await listen(ui);
});

after(() => {
  ui.closeAllConnections();
  fakeApi.closeAllConnections();
  ui.close();
  fakeApi.close();
});

describe("configuration", () => {
  test("defaults to the local API http profile", () => {
    const config = loadConfig({});
    assert.equal(config.backend.origin, "http://127.0.0.1:5056");
    assert.equal(config.v2Backend.origin, "http://127.0.0.1:5057");
    assert.equal(config.port, 4173);
    assert.equal(config.host, "127.0.0.1");
  });

  test("rejects a backend URL with a path, credentials or another scheme", () => {
    for (const value of ["http://127.0.0.1:5056/api", "http://user:pw@127.0.0.1:5056", "file:///c:/x", "ftp://host"]) {
      assert.throws(() => loadConfig({ WMS_API_BASE_URL: value }), /origin/);
      assert.throws(() => loadConfig({ WMS_V2_API_BASE_URL: value }), /origin/);
    }
  });

  test("explicit v2 origin overrides the shared explicit fallback", () => {
    assert.equal(loadConfig({ WMS_API_BASE_URL: "http://127.0.0.1:6001" }).v2Backend.port, "6001");
    const config = loadConfig({ WMS_API_BASE_URL: "http://127.0.0.1:6001", WMS_V2_API_BASE_URL: "http://127.0.0.1:6002" });
    assert.equal(config.backend.port, "6001");
    assert.equal(config.v2Backend.port, "6002");
  });
});

describe("static files", () => {
  test("serves the page with security headers", async () => {
    const response = await fetch(`${uiBase}/`);
    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type"), /text\/html/);
    assert.match(response.headers.get("content-security-policy"), /default-src 'self'/);
    assert.equal(response.headers.get("cache-control"), "no-store");
  });

  test("serves only the allow-listed files", async () => {
    for (const path of ["/server.mjs", "/package.json", "/test/server.test.mjs", "/..%2f..%2fREADME.md", "/%2e%2e/%2e%2e/global.json"]) {
      assert.equal((await fetch(`${uiBase}${path}`)).status, 404, path);
    }
    assert.equal((await fetch(`${uiBase}/api-client.js`)).status, 200);
  });
});

describe("proxy", () => {
  test("forwards an allowed GET with its query and streams the answer", async () => {
    seen.length = 0;
    const response = await fetch(`${uiBase}/api/v1/products?limit=200&cursor=abc`, {
      headers: { Authorization: "Bearer t", "X-Correlation-ID": "ui-1", Cookie: "session=1", "X-Forwarded-Host": "evil" }
    });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { items: [], nextCursor: null });
    assert.equal(response.headers.get("x-correlation-id"), "abc");
    assert.equal(response.headers.get("set-cookie"), null);
    assert.equal(response.headers.get("x-internal"), null);
    const forwarded = seen[0];
    assert.equal(forwarded.url, "/api/v1/products?limit=200&cursor=abc");
    assert.equal(forwarded.headers.authorization, "Bearer t");
    assert.equal(forwarded.headers["x-correlation-id"], "ui-1");
    assert.equal(forwarded.headers.cookie, undefined);
    assert.equal(forwarded.headers["x-forwarded-host"], undefined);
  });

  test("forwards a write with body and Idempotency-Key and preserves status and Retry-After", async () => {
    seen.length = 0;
    const response = await fetch(`${uiBase}/api/v1/inventory/receipts`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": "6f1c1d1e-0000-4000-8000-000000000001" },
      body: JSON.stringify({ locationId: "x", lines: [] })
    });
    assert.equal(response.status, 429);
    assert.equal(response.headers.get("retry-after"), "7");
    assert.equal(response.headers.get("idempotent-replayed"), "true");
    assert.equal(seen[0].headers["idempotency-key"], "6f1c1d1e-0000-4000-8000-000000000001");
    assert.equal(seen[0].body, '{"locationId":"x","lines":[]}');
  });

  test("preserves 401 and WWW-Authenticate", async () => {
    const response = await fetch(`${uiBase}/api/v1/auth/me`);
    assert.equal(response.status, 401);
    assert.equal(response.headers.get("www-authenticate"), "Bearer");
  });

  test("refuses paths and methods outside the allow-list without contacting the API", async () => {
    seen.length = 0;
    const cases = [
      ["GET", "/api/v1/inventory/receipts"], ["DELETE", "/api/v1/products"], ["GET", "/api/v3/products"],
      ["GET", "/api/v1/products/not-a-guid"], ["GET", "/api/v1/admin"], ["GET", "/api//evil.example/x"],
      ["GET", "/api/v1/products/../../openapi/v1.json"], ["GET", "/health/live"]
    ];
    for (const [method, path] of cases) {
      const response = await fetch(`${uiBase}${path}`, { method });
      assert.ok([404, 405].includes(response.status), `${method} ${path} -> ${response.status}`);
    }
    assert.equal(seen.length, 0);
  });

  test("rejects an oversized body with 413", async () => {
    const response = await fetch(`${uiBase}/api/v1/inventory/counts`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "x".repeat(70 * 1024) });
    assert.equal(response.status, 413);
    assert.equal((await response.json()).code, "PAYLOAD_TOO_LARGE");
  });

  test("answers 504 API_TIMEOUT when the API is too slow", async () => {
    const started = Date.now();
    const response = await fetch(`${uiBase}/api/v1/inventory/shipments`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
    assert.equal(response.status, 504);
    assert.match(response.headers.get("content-type"), /problem\+json/);
    assert.equal((await response.json()).code, "API_TIMEOUT");
    assert.ok(Date.now() - started < 1500);
  });

  test("answers 502 API_UNAVAILABLE when nothing listens on the backend", async () => {
    const closed = createServer();
    const base = await listen(closed);
    closed.close();
    await once(closed, "close");
    const proxy = createAppServer(loadConfig({ WMS_API_BASE_URL: base }), { logger: { log() {} } });
    const proxyBase = await listen(proxy);
    try {
      const response = await fetch(`${proxyBase}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
      assert.equal(response.status, 502);
      const problem = await response.json();
      assert.equal(problem.code, "API_UNAVAILABLE");
      assert.equal(problem.status, 502);
    } finally {
      proxy.close();
    }
  });

  test("logs method, path and status but never query strings, tokens or bodies", async () => {
    logs.length = 0;
    await fetch(`${uiBase}/api/v1/barcodes/resolve?value=SECRET-BARCODE`, { headers: { Authorization: "Bearer TOKEN-VALUE" } });
    await fetch(`${uiBase}/api/v1/auth/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: '{"password":"PW-VALUE"}' });
    const text = logs.join("\n");
    assert.match(text, /GET \/api\/v1\/barcodes\/resolve -> 401/);
    for (const secret of ["SECRET-BARCODE", "TOKEN-VALUE", "PW-VALUE"]) assert.ok(!text.includes(secret), secret);
  });
});
