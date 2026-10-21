# Test UI on the real API: acceptance

Date: 2026-10-15. Verdict: **PASS.** `prototypes/wms-test-ui` now uses the real WMS API v1 through a same-origin Node proxy. No mock data remains.

**Scope.** The browser test UI and its standard-library Node server only. No backend code, contract, `global.json` or package changed. There is no mobile, SignalR or production database work. The `dotnet` suite was re-run only as a no-regression check.

## Changed files

All under `prototypes/wms-test-ui/` unless noted.

| File | Change |
| --- | --- |
| `server.mjs` | Rewritten. Serves an allow-list of 4 static files with CSP. Same-origin reverse proxy to one configured backend (`WMS_API_BASE_URL`, default `http://127.0.0.1:5056`) for the 14 v1 operations the UI uses plus `GET /health/ready`. It filters headers in both directions, streams responses, caps bodies at 64 KB and enforces a timeout (`504 API_TIMEOUT`). An unreachable API gives `502 API_UNAVAILABLE`. Logs carry no query string or headers. Binds to `127.0.0.1` |
| `api-client.js` | New, DOM-free. Tokens in memory only. Single-flight refresh, at most one refresh-and-repeat on 401. Problem normalization (`errors`, `Retry-After`, correlation ID). Uncertain/retryable classification. A session epoch discards answers that arrive after sign-out. Bounded cursor paging |
| `app.js` | Rewritten. Sign-in, `/auth/me`, catalog and balance reads with loading/empty/error states, and barcode/SKU lookup. The 5 writes use frozen body + `randomUUID` key, a busy lock, and a pending-action retry with the same key and body. Countdown for `429`. Field errors for `400`. Permission-aware tabs. Balances are re-read after every write, and again automatically when health recovers. Sign-out clears state and DOM |
| `index.html`, `styles.css` | Sign-in view, real-data banner, API status, filters, catalog, pending banner, session-only history labelled as such. Mock alerts removed. Grid overflow fixed for narrow screens |
| `package.json` | `npm test` (`node --test`) |
| `start-demo.ps1` | New. Starts the API (http profile) in a new window, then the UI. No secrets |
| `test/server.test.mjs`, `test/api-client.test.mjs` | New `node:test` unit tests |
| `test/e2e/real-api-e2e.mjs`, `test/e2e/browser-smoke.mjs` | New end-to-end scripts against the built API |
| `README.md` | Rewritten: setup, config, token persistence, behaviour, tests, limits |
| `README.md` (root) | Pointer to the UI |
| `docs/test-ui-api-acceptance.md` | This report |

## Results

| Check | Result |
| --- | --- |
| `node --check` on all 7 JS/MJS files | OK |
| `npm test` (proxy 13, client 8) | **21/21 pass** |
| `node test/e2e/real-api-e2e.mjs` | **15/15 pass** (run 2×, the final run after all UI changes) |
| `node test/e2e/browser-smoke.mjs` (headless Edge, CDP via Node's built-in WebSocket) | **13/13 pass**; screenshots in git-ignored `artifacts/test-ui/` |
| `dotnet test Wms.sln` (no backend change) | **189/189 pass** (Domain 60, Application 12, Contract 25, Api 57, Sqlite 35) |

Both end-to-end scripts run the built `Wms.Api.dll` (`ASPNETCORE_ENVIRONMENT=Development`) on a new SQLite file in `%TEMP%`. They use a random seed password and JWT key, passed only through the child environment and never printed. Both delete the folder afterwards. `src/Wms.Api/App_Data` was never used and does not exist.

**Real API through the proxy** (`real-api-e2e.mjs`):
- Wrong password gives 401.
- Login and `/auth/me` work for all 4 roles.
- Refresh rotates the token; reusing the old refresh token gives 401.
- No token gives 401.
- Catalog reads: products, locations, both by-id reads, barcode resolve (and 404). Balances start empty.
- Receipt: 200, `RCV-…`. Same key and body replays with `Idempotent-Replayed: true` and the same number. Same key with another body gives `409 IDEMPOTENCY_CONFLICT`.
- `400` with `lines[0].quantity` and `Idempotency-Key` field errors.
- `403` for a receiver attempting a shipment or adjustment.
- Shipment, then `409 INSUFFICIENT_STOCK`.
- Movement, count and adjustment, with the expected balances.
- API stopped: `502 API_UNAVAILABLE` in < 5 s.
- Restart on the same file: balances, the pre-restart refresh token and the idempotent replay all survive.
- `429 RATE_LIMITED` with `Retry-After` passes through.
- No password, key or token appears in the API or proxy logs.

**Browser** (`browser-smoke.mjs`):
- Health label "已连接真实 WMS API". A wrong password shows a clear error and clears the password field.
- Empty-state message with no fake stock.
- A triple click on submit commits one receipt (balance 5).
- Shipment; `INSUFFICIENT_STOCK` message. Server `400` shown at the field (`quantityDelta`).
- Adjustment, movement and count.
- API stopped mid-session: the write becomes a pending action showing its key, submit is locked, and balances show an error state. After the restart, **retry** commits.
- Editing the form discards the pending action.
- "重新读取" recovers.
- Barcode lookup.
- Sign-out clears data, including the DOM. The shipper sees receive as not permitted.
- At 420 px there is no horizontal overflow.
- No browser exceptions.

## Issues found and fixed during verification

1. The history list was not re-rendered after a successful write (only the counter was).
2. A failed sign-in's cleanup overwrote the error message.
3. A balance reload rebuilt the operation form and lost user input.
4. The previous user's form stayed in the hidden DOM after sign-out.
5. After the API returned, the error state stayed until a manual reload. The page now reloads on health recovery.
6. A wide table stretched the grid on narrow screens.
7. The first E2E draft hung in its own `stopApi()`: a killed child has `signalCode`, not `exitCode`.

## Local availability at the end of this work

- **UI:** `http://localhost:4173` runs the new `server.mjs`. It was started detached; its log is in `artifacts/test-ui/ui-server.*.log`. The earlier process on 4173 (PID 43768, `node server.mjs`) was verified first. It was an orphaned copy of this folder's *old* server, serving the new files byte-identically, but it had no `/api` proxy (`/api/*` gave 404) and it exposed `server.mjs`. That made it non-functional for the new UI, so it was stopped and replaced.
- **API:** not running (nothing listens on 5056). The UI therefore shows "WMS API 不可达". It was not started here because that uses the user's own secrets and database. Start it with `dotnet run --project src/Wms.Api --launch-profile http` after the one-time user-secrets in `prototypes/wms-test-ui/README.md`.

## Remaining gaps

- The API has no logout or revoke endpoint: sign-out only forgets tokens locally. The refresh token stays valid server-side until it expires (8 h).
- There is no transaction history or alert API in v1. The page shows only this tab's successful writes and no alerts.
- Behind the proxy, anonymous requests (sign-in) share the API's per-address rate-limit budget of `127.0.0.1`.
- The UI is single-line per write (the API accepts up to 200 lines). It has no catalog or user administration.
- The proxy codes `API_UNAVAILABLE`, `API_TIMEOUT`, `NOT_PROXIED` and `PAYLOAD_TOO_LARGE` are UI-server codes, not part of the v1 contract.
- Browser verification used headless Edge only; no other browsers or manual accessibility review.
- Nothing was committed.
