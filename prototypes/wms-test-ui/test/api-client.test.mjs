import assert from "node:assert/strict";
import { test } from "node:test";
import { ApiError, createApiClient, isRetryable, readAll } from "../api-client.js";

const json = (status, body, headers = {}) => new Response(body === undefined ? null : JSON.stringify(body), {
  status, headers: { "Content-Type": "application/json", ...headers }
});
const login = (n = 1) => ({
  accessToken: `access-${n}`, refreshToken: `refresh-${n}`, accessTokenExpiresAt: "2026-10-14T00:15:00Z", refreshTokenExpiresAt: "2026-10-14T08:00:00Z",
  user: { id: "u", username: "receiver", displayName: "Receiving Clerk", role: "receivingClerk", permissions: ["Inventory.Receive"] }
});

/** A fake fetch that answers from a queue of handlers and records every request. */
function fakeFetch(handlers) {
  const calls = [];
  const fetchImpl = async (path, init) => {
    calls.push({ path, method: init.method, headers: init.headers, body: init.body });
    const handler = handlers.shift();
    if (!handler) throw new Error(`unexpected request ${path}`);
    return handler(path, init);
  };
  return { calls, fetchImpl };
}

test("signs in, keeps tokens in memory and sends the bearer token", async () => {
  const { calls, fetchImpl } = fakeFetch([() => json(200, login()), () => json(200, login().user)]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  assert.equal(client.user.username, "receiver");
  assert.ok(client.can("Inventory.Receive"));
  assert.ok(!client.can("Inventory.Ship"));
  await client.me();
  assert.equal(calls[1].headers.Authorization, "Bearer access-1");
  assert.match(calls[1].headers["X-Correlation-ID"], /^ui-[0-9a-f]{32}$/);
});

test("on 401 it refreshes once and repeats the identical write with the same key and body", async () => {
  const { calls, fetchImpl } = fakeFetch([
    () => json(200, login(1)),
    () => json(401, { code: "UNAUTHENTICATED" }),
    () => json(200, login(2)),
    () => json(200, { transactionNo: "RCV-1" }, { "Idempotent-Replayed": "true" })
  ]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  const result = await client.write("/api/v1/inventory/receipts", { a: 1 }, "key-1");
  assert.equal(result.data.transactionNo, "RCV-1");
  assert.equal(result.replayed, true);
  assert.equal(JSON.parse(calls[2].body).refreshToken, "refresh-1");
  assert.equal(calls[3].headers.Authorization, "Bearer access-2");
  assert.equal(calls[3].headers["Idempotency-Key"], "key-1");
  assert.equal(calls[3].body, calls[1].body);
});

test("concurrent 401s share a single refresh (a refresh token is single-use)", async () => {
  const { calls, fetchImpl } = fakeFetch([
    () => json(200, login(1)),
    () => json(401, {}), () => json(401, {}),
    () => json(200, login(2)),
    () => json(200, { items: [] }), () => json(200, { items: [] })
  ]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  await Promise.all([client.get("/api/v1/products"), client.get("/api/v1/locations")]);
  assert.equal(calls.filter(c => c.path === "/api/v1/auth/refresh").length, 1);
});

test("a rejected refresh ends the session, and a second 401 after refresh is not retried again", async () => {
  const ended = [];
  const first = fakeFetch([() => json(200, login()), () => json(401, {}), () => json(401, { code: "UNAUTHENTICATED" })]);
  const client = createApiClient({ fetchImpl: first.fetchImpl, onSessionEnded: reason => ended.push(reason) });
  await client.login("receiver", "pw");
  await assert.rejects(client.me(), error => error.status === 401);
  assert.deepEqual(ended, ["expired"]);
  assert.equal(client.signedIn, false);

  const second = fakeFetch([() => json(200, login()), () => json(401, {}), () => json(200, login(2)), () => json(401, {})]);
  const other = createApiClient({ fetchImpl: second.fetchImpl, onSessionEnded: reason => ended.push(reason) });
  await other.login("receiver", "pw");
  await assert.rejects(other.me(), error => error.status === 401);
  assert.equal(second.calls.length, 4);
  assert.equal(other.signedIn, false);
});

test("normalizes problem details, 429 Retry-After and field errors", async () => {
  const { fetchImpl } = fakeFetch([
    () => json(200, login()),
    () => json(429, { code: "RATE_LIMITED", title: "Too many" }, { "Retry-After": "12" }),
    () => json(400, { code: "VALIDATION_FAILED", errors: { "lines[0].quantity": ["Must be greater than zero."] } }),
    () => json(409, { code: "INSUFFICIENT_STOCK", detail: "Location A has 2." }),
    () => json(403, { code: "FORBIDDEN" })
  ]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  const rateLimited = await client.write("/w", {}, "k").catch(e => e);
  assert.equal(rateLimited.retryAfterSeconds, 12);
  assert.ok(isRetryable(rateLimited));
  const invalid = await client.write("/w", {}, "k").catch(e => e);
  assert.deepEqual(invalid.errors, { "lines[0].quantity": ["Must be greater than zero."] });
  assert.ok(!isRetryable(invalid));
  const stock = await client.write("/w", {}, "k").catch(e => e);
  assert.equal(stock.code, "INSUFFICIENT_STOCK");
  assert.ok(!isRetryable(stock));
  const forbidden = await client.write("/w", {}, "k").catch(e => e);
  assert.equal(forbidden.status, 403);
  assert.ok(!isRetryable(forbidden));
});

test("network failures, timeouts and proxy 502/504 are uncertain and retryable", async () => {
  const { fetchImpl } = fakeFetch([
    () => json(200, login()),
    () => { throw new TypeError("fetch failed"); },
    () => json(504, { code: "API_TIMEOUT" }),
    () => json(502, { code: "API_UNAVAILABLE" })
  ]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  for (const expected of ["NETWORK_ERROR", "API_TIMEOUT", "API_UNAVAILABLE"]) {
    const error = await client.write("/w", {}, "k").catch(e => e);
    assert.equal(error.code, expected);
    assert.ok(error.uncertain && isRetryable(error));
  }
});

test("a request that never answers is aborted by the client timeout", async () => {
  const fetchImpl = (path, init) => new Promise((resolve, reject) => {
    if (path.endsWith("login")) return resolve(json(200, login()));
    init.signal.addEventListener("abort", () => reject(init.signal.reason));
  });
  const client = createApiClient({ fetchImpl, timeoutMs: 100 });
  await client.login("receiver", "pw");
  const started = Date.now();
  const keepAlive = setTimeout(() => {}, 5000); // AbortSignal.timeout does not keep the process alive by itself
  const error = await client.write("/w", {}, "k").catch(e => e);
  clearTimeout(keepAlive);
  assert.equal(error.code, "API_TIMEOUT");
  assert.ok(Date.now() - started < 1000);
});

test("sign-out clears the session and discards answers of requests started before it", async () => {
  let release;
  const ended = [];
  const fetchImpl = path => path.endsWith("login")
    ? Promise.resolve(json(200, login()))
    : new Promise(resolve => { release = () => resolve(json(200, { items: [] })); });
  const client = createApiClient({ fetchImpl, onSessionEnded: reason => ended.push(reason) });
  await client.login("receiver", "pw");
  const pending = client.get("/api/v1/products");
  await new Promise(resolve => setTimeout(resolve, 0));
  client.logout();
  release();
  await assert.rejects(pending, error => error.code === "SESSION_CHANGED");
  assert.equal(client.user, null);
  assert.deepEqual(ended, ["signedOut"]);
  await assert.rejects(client.me(), error => error.status === 401);
});

test("readAll follows cursors and stops at the page bound", async () => {
  const pages = [{ items: [1, 2], nextCursor: "c1" }, { items: [3] }];
  const { calls, fetchImpl } = fakeFetch([() => json(200, login()), ...pages.map(p => () => json(200, p))]);
  const client = createApiClient({ fetchImpl });
  await client.login("receiver", "pw");
  assert.deepEqual(await readAll(client, "/api/v1/products"), [1, 2, 3]);
  assert.equal(calls[1].path, "/api/v1/products?limit=200");
  assert.equal(calls[2].path, "/api/v1/products?limit=200&cursor=c1");

  const endless = createApiClient({ fetchImpl: path => Promise.resolve(path.endsWith("login") ? json(200, login()) : json(200, { items: [0], nextCursor: "again" })) });
  await endless.login("receiver", "pw");
  await assert.rejects(readAll(endless, "/api/v1/products", {}, 3), error => error instanceof ApiError && error.code === "TOO_MANY_PAGES");
});
