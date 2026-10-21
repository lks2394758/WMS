# WMS Test UI

## V2 redesign frontend (local demonstration prototype)

The new buildless UI is at `http://localhost:4173/v2/` after `npm start`. The original `/` page and v1 API client are retained. No additional packages or framework are required.

**Status:** B1 identity, B2a catalog, B2b bins/admin, B3a inventory/ledger, B3b serial query/correction, B4 count confirmation/investigation and B5a dashboard/alerts/activity queries have passed independent acceptance (latest 2026-10-20). Backend by Claude, frontend and independent verification by Codex. Framework-level failure auditing and final automated prototype acceptance have also passed; see `docs/v2-prototype-acceptance.zh-CN.md`. Manual and production acceptance are not claimed. The authoritative wire contract is `contracts/v2/README.md`; progress is in `docs/v2-backend-handoff.zh-CN.md`. Do not mistake contract-fixture tests for real WMS acceptance.

- New modules: `v2-app.js`, `v2-model.js`, `v2-client.js`, `v2-proxy.mjs`, `v2-labels.js`, `v2.html`, `v2.css`.
- Pages/forms: dashboard, products and stock distribution, bins/list-diagram and rack creation, multi-line inventory operations with receipt inline product creation, counts/recounts/investigations, serial correction, transactions, alerts, activity filters, categories/units, users and settings.
- Browser requests only `/api/v2`. It never falls back to v1, mock data, or browser stock storage. Until the real v2 API is ready, authentication/data reads fail explicitly.
- Login credential is an HttpOnly, SameSite=Strict cookie on the local Node proxy. Browser refresh/reopen retains a still-valid cookie; authenticated requests still depend on the backend session state. The credential is not placed in JSON returned to the browser, localStorage or sessionStorage. Local HTTP is loopback-only; a remote deployment would require TLS and Secure cookies.
- New proxy validates write origin, requires JSON, buffers/limits requests before forwarding, and only exposes allow-listed routes. Backend authorization is still mandatory. The v2 request limit is 3 MiB to accommodate an optional 2 MiB base64 image; v1 retains its 64 KiB limit.
- Decimal quantities/IDs stay strings. Client validation uses BigInt for range checks, not floating point for stock. The backend remains authoritative.
- Uncertain writes retain a frozen payload and original idempotency key in memory. Abandoning a pending request does not cancel an already-committed operation. The UI tells the operator to inspect history before submitting a new action.
- Labels render locally as Code 128 subset B (printable ASCII) or QR byte mode with explicit UTF-8 ECI. The QR implementation is adapted from Project Nayuki's MIT-licensed algorithm; its license is retained in `v2-labels.js`. Both label formats preserve the current server-provided code; non-ASCII values require QR. Six fixed-mask matrices, including UTF-8 and long values, were compared to the upstream reference. Preview/print controls have browser tests; no physical printer/scanner acceptance is claimed.
- Product edit/details display the saved PNG/JPEG, and selected uploads can be previewed. Image removal is an explicit `removeImage` update, mutually exclusive with a new upload. The serial picker reads actual in-stock records for a selected product/bin; the service must revalidate them when submitting. Rack diagrams group the current page by zone, rack and level; they are not a complete-warehouse map unless all relevant bins are on that page.
- Adjustments may explicitly link a count-result ID for an investigation. This is not a task ID and does not automatically apply an old discrepancy; the backend must validate the current stock and result association.
- Remaining acceptance: manual business checks, physical printer/scanner and production provider/deployment. B0–B5 automated prototype acceptance passed; this is not a production-certified WMS.

Verification:

Final B5b automated prototype gate (2026-10-20): backend **545 passed** in the documented full-plus-focused rerun; all seven existing real business/API/browser scripts were rerun, **62 passed**. `node tools/v2-framework-audit-e2e.mjs` from the repository root adds **7 real Kestrel audit checks**. Existing 51 client/proxy and 18 contract-fixture results are historical baselines and were not rerun in this phase. No UI logic changed; all warehouse/profile data were temporary. See `docs/v2-prototype-acceptance.zh-CN.md` for audit failure limits and manual v2 startup; no demonstration service was left running by this test run.

Latest B5a acceptance: `node test/e2e/v2-real-monitoring-browser.mjs` has **8 passing real API/browser checks**, using only temporary warehouse/profile data. Dashboard shows investigation count and all/mine activity; alerts support status/type/product/bin filters and immutable trigger evidence; history supports structured subject, result, actor and UTC-range filters. Selected controls remain visible after refresh. Backend **541 passed**, build zero warnings/errors, V2 whitespace verified; client/proxy **51 passed**, browser contract fixture **18 passed** (not a real warehouse). Framework-level failures still await B5b auditing. Real screenshot: `artifacts/v2-real-ui/monitoring.png`.

Latest B4 acceptance: `node test/e2e/v2-real-count-browser.mjs` has **7 passing real API/browser checks** (blind start/freeze/replay, cancelled recount restoration, exact confirmation adjustment/replay, serial investigation preserved after zero-difference recount, invalid adjustment/role rejection, browser confirmation and explicit investigation resolution). It reuses the existing harness/browser with a temporary database, no new dependencies or hosting. Backend **536 passed**, build zero warnings/errors, V2 whitespace verified. Prior scripts' figures below are historical baselines; the unaffected old browser/client suites were not rerun in this phase.

`node test/e2e/v2-real-inventory-browser.mjs` has **15 passing real API/browser checks**, including exact packaging, rollback, capacity reset, immutable evidence, same-key concurrency, oversell rejection, Unicode ledger pagination and current physical item return IDs. It runs only a temporary warehouse. Operation mode is retained after save, failed forms stay editable, and actor roles/times in ledger details are translated/localized. Screenshot: `artifacts/v2-real-ui/inventory.png`. Original B3a baseline: 511 backend tests.

`node test/e2e/v2-real-serial-browser.mjs` has **9 passing real API/browser checks**: exact filters, current/historical Unicode search and paging, old-number warnings/refusal, immutable event-time history, correction without stock changes, version-before-no-op, globally reserved identifiers, current physical identity on return and the actual in-stock shipment picker. Screenshot: `artifacts/v2-real-ui/serials.png`. Historical B3b baseline: backend **521 passed**, client/proxy **51 passed**, browser fixture **18 passed**, real API checks **47 passed** across the then-existing five scripts. All warehouse/profile data are temporary. For latest count acceptance see B4 above; monitoring and full-system acceptance remain pending.

```powershell
npm test
node test/e2e/v2-browser-smoke.mjs
```

The first runs client/proxy unit tests, including v1 regressions. The second uses an isolated HTTP contract fixture and the existing headless Edge/Chrome; it never starts a real WMS API or touches a database. Screenshots are generated under repository `artifacts/v2-ui`. No fixture server or fake stock is served by `npm start`.

Real v2 authentication/proxy acceptance has a separate script, `node test/e2e/v2-real-auth-e2e.mjs` (first build `src/Wms.Api` from the repository root). It starts the built API on a new temporary SQLite file with a random in-memory seed password, verifies login/session restart/revocation/logout and checks that a temporary legacy sentinel file is untouched. All 7 checks passed on 2026-10-19, including real declared and chunked oversized requests returning 413. It does not use the existing demonstration databases and does not prove inventory/count workflows. These results are separate from the 18 browser contract-fixture checks and 49 client/proxy tests.

`node test/e2e/v2-real-catalog-browser.mjs` exercises real catalog APIs and the actual browser UI on a new temporary database. All 6 checks passed, including Unicode name search/pagination. The shared `v2-real-harness.mjs` uses the existing browser and cleans up its isolated profile and processes. Login is only enabled after the JavaScript handler is attached, and its HTML fallback is POST, never credentials in a URL. Dialog requests also discard superseded replies so a late detail cannot replace a newer editor. Client/proxy regression has 49 passing tests; browser contract-fixture checks have 18.

`node test/e2e/v2-real-admin-browser.mjs` has **10 passing real B2b checks**: bin create/edit/history/diagram, settings and rack defaults, account creation and password-free editing, atomic rack collision rejection, Unicode cursor queries, session invalidation after reset/role changes/disable, last-administrator protection, settings version conflicts, current-role permissions and new-versus-existing login expiry. The tenth check verifies conflict labels appear as plain text and the rejected form stays open. Related IDs remain exact strings; visible summaries show at most ten objects without dropping the original response evidence. It uses only a temporary database and existing browser; screenshot: `artifacts/v2-real-ui/accounts.png`. B2b was independently accepted on 2026-10-20 after Claude's review fixes; see `docs/v2-b2b-admin.md`. Latest client/proxy regression: **51 passed**; browser contract fixture: **18 passed**. These checks do not validate stock/count workflows or every alert transition. The 49-test counts above record the earlier B1/B2a acceptance baseline.

## Original v1 UI

A zero-dependency browser UI (vanilla HTML/CSS/JS, Node 22 standard library only) for the **real** WMS API v1. It signs in, reads the catalog and committed balances, and sends receipts, shipments, movements, counts and adjustments. The API validates and commits every write; the page never keeps or computes stock of its own and re-reads balances after each write. There is no mock data and no fallback: if the API is down, the page says so.

## Run

One-time setup (from the repository root). The secrets are stored in your user profile, never in the repository:

```powershell
dotnet user-secrets --project src/Wms.Api set "DevelopmentSeed:Password" "<choose a local password>"
dotnet user-secrets --project src/Wms.Api set "Jwt:SigningKey" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
```

Every time, in two terminals:

```powershell
dotnet run --project src/Wms.Api --launch-profile http     # API on http://localhost:5056
cd prototypes/wms-test-ui; npm start                         # UI on http://localhost:4173
```

or, in one step, `./prototypes/wms-test-ui/start-demo.ps1` (opens the API in a new window, then runs the UI; `-UiOnly` if the API already runs).

Open `http://localhost:4173` and sign in as `admin`, `supervisor`, `receiver` or `shipper` with the `DevelopmentSeed:Password` you chose. The demo database holds no stock at first: start with a receipt.

| Role | Can write |
| --- | --- |
| `admin`, `supervisor` | receipt, movement, shipment, count, adjustment |
| `receiver` | receipt, movement |
| `shipper` | shipment |

## Configuration (environment variables)

| Variable | Default | Meaning |
| --- | --- | --- |
| `WMS_API_BASE_URL` | `http://127.0.0.1:5056` | v1 backend origin. If explicitly supplied, also serves as the v2 fallback unless overridden below. Must be an http(s) origin without path, query or credentials |
| `WMS_V2_API_BASE_URL` | `http://127.0.0.1:5057` | Independent v2 backend origin; takes priority over the explicit v1 fallback. Same origin restrictions. No automatic fallback to a different backend on failure |
| `PORT` | `4173` | UI port |
| `HOST` | `127.0.0.1` | Bind address. Loopback only by default |
| `WMS_PROXY_TIMEOUT_MS` | `15000` | Upstream timeout; then `504 API_TIMEOUT` |

## How it connects

- `server.mjs` serves only `index.html`, `app.js`, `api-client.js` and `styles.css`, with a strict Content-Security-Policy. It is also a same-origin reverse proxy, so the browser needs no CORS.
- The proxy forwards only the v1 endpoints the UI uses (auth login/refresh/me, products, locations, barcode resolve, balances, the five inventory writes) and `GET /health/ready`, and only to `WMS_API_BASE_URL`. Any other path or method gets `404 NOT_PROXIED` without touching the API. It forwards only `Authorization`, `Content-Type`, `Accept`, `Idempotency-Key` and `X-Correlation-ID`. It returns status, body (streamed), `Content-Type`, `Retry-After`, `Idempotent-Replayed`, `X-Correlation-ID` and `WWW-Authenticate`. Bodies are limited to 64 KB.
- If the API cannot be reached, the proxy answers `502` with code `API_UNAVAILABLE`; if it does not answer in time, `504` with `API_TIMEOUT`. Both are `application/problem+json`. These codes come from the proxy, not the API contract.
- The proxy log shows method, path (no query string), status and duration. It never logs headers, tokens, passwords or bodies.

## Session, tokens and writes

- Access and refresh tokens live **only in JavaScript memory**. Nothing goes to `localStorage`, `sessionStorage` or cookies. Reloading the tab or signing out forgets them, and you sign in again.
- On a `401`, the client refreshes **once** (concurrent requests share one refresh, because a refresh token is single-use), then repeats the identical request. If the refresh is rejected, the session ends and the sign-in view returns.
- Sign-out clears tokens, the pending action, the session history and all rendered data. The API has no logout endpoint, so the refresh token stays valid on the server until it expires (8 h).
- Each submit is one user action with a new `crypto.randomUUID()` `Idempotency-Key`. The button is disabled while a request runs, so double clicks send nothing extra.
- If a write's outcome is uncertain (network error, timeout, `502/504`, `500`) or it may be retried (`409 CONCURRENCY_CONFLICT`, `429` after `Retry-After`), it becomes a pending action. **Retry** resends the frozen body with the same key. Editing the form, switching operation or discarding it ends the pending action; the next submit is a new action with a new key.
- `400` field errors appear next to the fields. `403`, `404`, `409 INSUFFICIENT_STOCK / IDEMPOTENCY_CONFLICT` and `422` are shown as final.
- "本标签页成功提交的操作" lists only this tab's successful writes, from the server's responses. It is not a server ledger: v1 has no transaction history endpoint.
- No stock alerts are shown. v1 has no alert endpoint, and the page does not invent thresholds.

## Tests

```powershell
cd prototypes/wms-test-ui; npm test                        # proxy + API client unit tests (node:test), no API needed
dotnet build src/Wms.Api                                     # from the repository root, before the two below
node prototypes/wms-test-ui/test/e2e/real-api-e2e.mjs        # real API behind the proxy: all endpoints, roles, replay, restart, 429, 502
node prototypes/wms-test-ui/test/e2e/browser-smoke.mjs       # headless Edge/Chrome drives the real UI; screenshots to artifacts/test-ui
```

The two end-to-end scripts start the built API on a new SQLite file in a temporary folder, with a random seed password and signing key that are never printed. They never touch `src/Wms.Api/App_Data`, and they delete the temporary folder afterwards.

## Limits

- This is a test UI and not the admin UI. It has no catalog or user management, no transaction history and no SignalR.
- The API's anonymous rate limit is per client address. Behind the proxy, every sign-in comes from `127.0.0.1` and shares one budget. Signed-in requests are limited per user as usual.
- Bound to loopback. Do not expose it on a network: it has no TLS of its own.
