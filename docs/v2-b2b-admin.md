# API v2 — B2b: bins and racks, users, settings

Status: **independently accepted on 2026-10-20** after Claude's review fixes and Codex's build, full backend tests, real B2b UI/API checks and login/catalog regressions. SQLite evidence only (AGENTS.md rule 4): nothing here is a production concurrency claim. Endpoints, DTOs and codes: `contracts/v2/README.md` §1, §2, §5, §6. No schema change (the frozen initial migration holds every column used); no new business table.

## Layers

| Layer | Files |
| --- | --- |
| Ports | `Wms.Persistence.Abstractions/V2/AdminPortsV2.cs` (`IV2AdminReader`; `IV2AdminWriteOperations`, part of the single `IV2CatalogWriteScope`) |
| Rules | `Wms.Application/V2/Admin/V2BinService.cs`, `V2UserService.cs`, `V2SettingsService.cs`; `V2/Common/V2CapacityAlerts.cs` (shared capacity-alert rule) |
| SQLite | `Wms.Persistence.Sqlite/V2/V2SqliteAdmin.cs` (reads; writes inside the existing catalog write scope) |
| HTTP | `Wms.Api/V2/V2AdminControllers.cs` (thin), registered only in `V2ControllerFeatureProvider` |

All writes use the B2a writer pattern on **one** v2 writer queue and one `BEGIN IMMEDIATE` transaction (no second lock): the clock is read after the lock; the session, the current role (never the union of roles), the permission and the versions are re-checked inside the transaction; the change, any session revocation and the success log commit together; a failure rolls back and is logged separately with the identity verified in the transaction.

## Bins and racks

- Positions: zone, rack, level, cell 1-16 characters, no whitespace, control characters or `-`; code = the four joined by `-`. Unique by Unicode invariant upper case against every current and historical code of every bin, archived included. Historical codes never address a bin.
- Lists in code order; `search` (code, current product name, SKU), `zone`, `rack`, `level` (case-insensitive), `productId`. Matching is done in application code (SQLite folds ASCII only) before the page is cut.
- `PUT` (move) and `DELETE` (archive) need zero stock (`STOCK_NOT_ZERO`) and no frozen or open count and no unresolved investigation (`OPEN_COUNT_OR_INVESTIGATION`, with the bin and the count task in `subjects`). Moving appends the old code to the history; archiving resolves the bin's capacity alerts and keeps the position reserved.
- `POST /bins/racks`: omitted sizes come from the settings read inside the transaction; 1-100 each (≤ 10 000 bins); labels `01`…`99`, `100`. Any taken position rejects the whole rack (`ALREADY_EXISTS`, up to 50 conflicting bins in `subjects`). The success log entry names **every** created bin (all 10 000 for a 100 × 100 rack, no truncation), so a later per-bin history finds it.

## Users

- Readable and writable only by a session whose current role is `systemAdministrator`. No deletion, no self-registration, no self-service password change.
- Username 1-64 without whitespace/control characters, unique by Unicode invariant upper case; display name 1-100; roles non-empty, distinct, fixed values; password 8-256, hashed with the existing PBKDF2 hasher outside the lock. Passwords and hashes never appear in responses or logs.
- A role change or disabling, and every password reset, revoke the account's open sessions **in the same transaction** and bump its version (the B1 login expected-version check then also rejects a login that verified the old password). A display-name change keeps the session; `/auth/me` shows the new name.
- `LAST_ADMINISTRATOR`: no change may leave no enabled account with `systemAdministrator`, the caller's own account included. Writes are serialized; a request that waited behind a change revoking its session is rejected by the in-transaction check (`SESSION_REVOKED`). Tested with a gated queue and with two unsynchronized concurrent requests on a real temporary SQLite file; this is SQLite evidence only.
- `hasActiveSession`: an open session (not logged out, not revoked, not expired now).

## Settings

- `GET` for every signed-in user; `PUT` only for a current `systemAdministrator`, with the stored `version`.
- Rack defaults 1-100 (later racks only); `sessionHours` 1-168 (new logins only; existing `ExpiresAt` never changes); thresholds with at most 2 decimals, 0 ≤ low < high ≤ 100.
- New default thresholds are checked in the same transaction against every bin with its own override; any effective low ≥ effective high rejects the whole change (`THRESHOLD_CONFLICT`, bins in `subjects`); no override is removed and nothing is half saved.
- After a threshold change all capacity alerts are re-evaluated (over capacity > high > low > normal, strict bounds; no product or capacity → none; a zero-stock bin that kept product and capacity is at 0 %). A continuing state keeps its alert and original snapshot; a change resolves the old and opens one of the new type with a snapshot of quantity, capacity, occupancy, effective thresholds, unit, precision and SKU (immutable by trigger). Count investigations are never closed here. The success log names every alert opened or resolved together with its bin and product; bins whose state continued are not named.

## Known limitations (deferred to B5)

- Requests rejected before the use case runs are **not** in the activity log: framework model binding/JSON errors (400), the authorization policy 403 for a role without the permission, 401, rate limiting (429) and the body size limit (413). They appear only in the request log. A cross-cutting audit for them is planned for B5.
- Activity log, alert, dashboard and history query endpoints do not exist yet (B5); the subjects stored now are what those filters will use.
- Inventory (B3) and counts (B4) are not implemented; their states are created with SQL in the tests.

## Independent acceptance results (Codex, 2026-10-20)

- Build: zero warnings/errors; V2 whitespace verification passed.
- Full backend: **471 passed**, zero failed/skipped (Domain 60, Application 12, Contracts 205, API 81, SQLite 113). This includes all 10 000 rack subjects, middle-bin history, unchanged continuing alert snapshots, settings alert/bin/product subjects, frozen/investigation HTTP codes, actual gated queued authorization checks and concurrent administrator writes.
- The first run found one test SQL ambiguity (`Type` from both `ActivityLogs` and `json_each`); Claude qualified the columns. Final tests were rebuilt and rerun without omitting or weakening that assertion.
- Real HTTP/BFF: authentication 7, catalog/browser 6, bins/admin/browser **10 passed**. All use temporary new databases and in-memory random credentials; the old-database sentinel was unchanged. No default user database was initialized.
- Frontend client/proxy: **51 passed**; browser contract fixture: **18 passed**. The new tenth real B2b check verifies that a rejected rack form stays open and names the conflicting bins as safe plain text.
- Owned test API, proxy and isolated browser processes were stopped. Screenshots were visually checked. No framework, package, database service or deployment was added.

Re-run backend checks:

```powershell
dotnet build Wms.sln --no-restore
dotnet test tests/Wms.Persistence.Sqlite.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test tests/Wms.Api.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test Wms.sln --no-build
```
