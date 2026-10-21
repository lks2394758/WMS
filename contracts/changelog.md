# Contract changelog

## v2 (partly served in the opt-in v2 process mode)

- B5b (accepted 2026-10-20): framework-rejected known v2 mutations now append one sanitized immutable failure log (400/415/401/403/429/413), preserving responses and existing business logs. No DTO/schema changes. Final automated prototype gate: backend 545, real business/browser 62, real Kestrel audit 7 passed; see `docs/v2-prototype-acceptance.zh-CN.md` for scope and limits.

- B5a (independently accepted 2026-10-20): authenticated read-only dashboard/alerts/activity-logs are served with unchanged DTOs, Unicode search before pagination, exact subject filters and one dashboard snapshot. Reversed UTC time ranges return 400; equal bounds remain empty. Backend 541 passed; real monitoring API/browser 8 passed. Framework-level failure auditing and full-system acceptance remain B5b. See `docs/v2-b5-monitoring.md`.

- B4b (independently accepted 2026-10-20): count confirm/resolve now served with unchanged B0 DTOs. Confirm is replay-safe, atomically adjusts untracked differences or opens serial investigations without stock changes; resolve records all unresolved results and closes discrepancy evidence. External detail reads share one SQLite snapshot. Backend 536 passed; real count API/browser 7 passed. Monitoring remains B5. See `docs/v2-b4-counts.md`.

- B4a (independently accepted 2026-10-20): count list/detail/start/submit/recount/cancel are served with unchanged B0 DTOs, blind snapshots, immutable results, start idempotency, selected-role guards and cancel restoration. Confirm/resolve remain B4b and are not served or advertised in allowedActions. Backend regression: 529 passed. See `docs/v2-b4-counts.md`.

API v2 is a new, separate contract under `/api/v2` for the redesigned database. It does not change any v1 type, route, example or OpenAPI document. Details: [contracts/v2/README.md](v2/README.md).

- B3b (independently accepted 2026-10-20): `/api/v2/serialized-items` (list with current/historical Unicode search and exact filters, detail with event-time history and corrections) and `/{id}/correct` are served with the B0 DTOs unchanged; the `version` is checked first (stale → `409 VERSION_CONFLICT`, also for the current number); with the current version, a correction to the current number is a no-op `200`; a case-only change is `ALREADY_EXISTS`. This completes the earlier B3b groundwork. Details: `docs/v2-b3-serials.md`.
- B3a (independently accepted 2026-10-20): `/api/v2/inventory/receipts|shipments|movements|adjustments` (`201`, `Idempotency-Key` required, replay `201` + `Idempotent-Replayed: true`) and `/api/v2/inventory/transactions` (+ `/{id}`) are served with the B0 DTOs unchanged. Serial rules for the four operations are included; query/correction endpoints are accepted in B3b above. Details: `docs/v2-b3-inventory.md`.

- B2b (independently accepted 2026-10-20): `/api/v2/bins` (+ `/bins/racks`), `/users` (+ `/{id}/reset-password`), `/settings` are served with the B0 DTOs unchanged. Moving or archiving a bin with an open count or unresolved investigation answers `409 OPEN_COUNT_OR_INVESTIGATION` (bin and count task in `subjects`), like the product rule; `BIN_FROZEN`/`INVESTIGATION_UNRESOLVED` stay reserved for inventory operations. Activity log subjects: a rack names every created bin; a settings change names every alert it opened or resolved with its bin and product. Details: `docs/v2-b2b-admin.md`.

- B0a: transport-only C# contracts in `src/Wms.Contracts/V2` (Common, Auth); examples in `contracts/examples/v2`; tests in `tests/Wms.ContractTests/V2`. No endpoint is implemented yet.
- Breaking against v1: IDs are positive 64-bit integers as JSON strings (not UUIDs); exact quantities are decimal strings (not `int`); one opaque session token with `expiresAt` replaces access/refresh tokens; the user carries `roles` plus a persisted `currentRole`.
- New v2 problem codes: `INVALID_CREDENTIALS`, `SESSION_EXPIRED`, `SESSION_REVOKED`, `ROLE_NOT_ASSIGNED`, `VERSION_CONFLICT`, `PAYLOAD_TOO_LARGE`. v2 problems may carry `subjects`.
- B0b: contracts for units, categories, products (image detail, `removeImage`), bins and racks, users and password reset, settings, multi-line inventory operations and transaction history, serialized items and correction, count tasks (start, submit, confirm, recount, cancel, resolve), alerts, activity logs and dashboard; `DeleteRequest`, `WirePercent`; business problem codes. Every endpoint, permission, retry rule and the differences from the frontend draft are in `contracts/v2/README.md`. Still no endpoint is served.
- B1 (contract and storage; endpoints in B2/B4): `POST /products` and `POST /counts` **require** `Idempotency-Key`; the key and body digest are persisted, immutable, on the created row, so a retry returns the existing product/task instead of creating a second one (replaces the B0 "known gap"). No DTO change.
- B2a review: product search is Unicode case-insensitive for names as specified; validation rejections of catalog writes are logged; failure logs name the identity verified inside the write transaction. No wire change.
- B2a: `/api/v2/units` (+ `GET /units/{id}`, additive), `/categories`, `/products` are served with the B0 DTOs unchanged. A product create replay answers `201` with the existing product and `Idempotent-Replayed: true`. Problems may carry `errors` for the named field also with non-validation codes (e.g. `ALREADY_EXISTS` on `sku`), as specified in B0.
- B1: a login whose account changed between password check and session write (password reset, disable, role change, any edit) answers `409 CONCURRENCY_CONFLICT` and opens no session; unmapped write conflicts answer 409, never 500; bodies over 4 MiB answer `413 PAYLOAD_TOO_LARGE`.
- B1: the v2 process mode (`V2:Enabled=true`) serves `/api/v2/auth/login|me|switch-role|logout` on the separate database `App_Data/wms-v2-demo.db`. Other v2 endpoints are not served yet (404). v1 runtime and contracts unchanged.

## v1 (unreleased)

- Initial transport contracts: auth, catalog, inventory requests/responses, alerts, problem details, `inventory.changed.v1`.
- Added beyond the guide's minimum: `RefreshRequest`, `AlertResponse`, error codes `LOCATION_INACTIVE` and `NOT_FOUND`.
- Inventory write requests carry a `lines` array, so one transaction can cover several products.
- M3: `contracts/openapi/wms.v1.json` was regenerated from the API; `OpenApiContractTests` in `tests/Wms.Api.Tests` detects semantic drift between it and the runtime contract.
- M3: `/health/ready` runs the named checks `persistence` and `jwt-signing` and answers `503 Unhealthy` when one fails; `/health/live` runs none. Neither is part of the OpenAPI document. No OpenAPI change.
- M3: a missing request body is reported only under the `errors` key `body` (it was also reported under the server's parameter name `request`).
- SQLite demonstration persistence: inventory writes now answer `409 CONCURRENCY_CONFLICT` (an existing v1 code) when the write lock is not obtained within `Persistence:Sqlite:LockTimeoutSeconds`; nothing is stored and the same `Idempotency-Key` may be retried. No OpenAPI change.
- M2: auth, catalog, balance and inventory-write endpoints (see `docs/api-conventions.md`). Movements require `Inventory.Receive`.
- M2: response header `Idempotent-Replayed: true` on a replayed inventory write (`ApiHeaders.IdempotentReplayed`).
