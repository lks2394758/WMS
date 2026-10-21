# API v2 — B1: database, initialization and identity

Status: **B1 independently accepted by Codex on 2026-10-19**. Whole-solution build: zero warnings/errors; full backend regression: 398 passed (Domain 60, Application 12, Contracts 205, API 70, SQLite 51). Real Kestrel + UI proxy + isolated SQLite checks: 7 passed, including session restart/revocation and declared/chunked oversized requests returning 413. Browser contract-fixture checks remain separate from real API acceptance. Catalog/inventory/count endpoints are not covered by B1. SQLite evidence only (AGENTS.md rule 4).

## Running the v2 process

v2 is an opt-in process mode of `Wms.Api`. Without `V2:Enabled=true` the API runs exactly as before (v1, `wms-demo.db`).

```powershell
# Once per machine: the password the missing v2 demo accounts receive (8-256 characters). Never committed.
dotnet user-secrets --project src/Wms.Api set "V2:Seed:Password" "<choose a local password>"

# Start v2 on its own port; a v1 process may keep running on 5056.
# Development loads user-secrets; outside Development pass V2__Seed__Password through the environment instead.
$env:ASPNETCORE_ENVIRONMENT = "Development"; $env:V2__Enabled = "true"; $env:ASPNETCORE_URLS = "http://localhost:5057"
dotnet run --project src/Wms.Api --no-launch-profile
```

| Key | Default | Meaning |
| --- | --- | --- |
| `V2:Enabled` | `false` | `true` starts the v2 mode: only `/api/v2/auth/*`, `/health/live`, `/health/ready`; no v1 endpoint, no JWT, no OpenAPI v1 |
| `V2:Sqlite:DataSource` | `App_Data/wms-v2-demo.db` | v2 file, relative to the content root. A file named `wms-demo.db` or equal to `Persistence:Sqlite:DataSource` is refused at startup |
| `V2:Sqlite:LockTimeoutSeconds` | `10` | Writer wait before `409 CONCURRENCY_CONFLICT` |
| `V2:Seed:Enabled` | `true` | Insert-missing seed at startup |
| `V2:Seed:Password` | none | Password for **missing** accounts only. Unset or shorter than 8: no account is created and startup logs a warning naming the missing accounts (never the value). Existing accounts and passwords are never changed |

Startup order: resolve and check the v2 path → create the directory → apply the v2 migration → WAL → seed (accounts `admin`, `supervisor`, `receiver`, `shipper`, one role each; unit "件"; settings: 4 levels, 6 cells per level, thresholds 20 %/80 %, 8 h sessions). No products, bins, stock or ledger. The v1 file is never opened, migrated, seeded or read by health checks.

Request bodies are limited to 4 MiB: a declared larger body gets `413 PAYLOAD_TOO_LARGE` before it is read (middleware, also under the in-memory test server); Kestrel's own limit is set to the same value for chunked bodies; when Kestrel stops such a body while it is being read it throws `BadHttpRequestException` with status 413, which `V2ExceptionHandler` answers as `413 PAYLOAD_TOO_LARGE` (only that status; other bad requests and JSON errors keep their own handling). The in-memory test server has no body limit, so the chunked path is verified by the real-Kestrel script `prototypes/wms-test-ui/test/e2e/v2-real-auth-e2e.mjs`; the API tests cover the mapping with a simulated exception.

Reset the v2 demo: stop the v2 process, delete `App_Data/wms-v2-demo.db*`. The v1 file is unaffected.

## Schema: 16 business tables plus framework tables

The v2 file holds exactly the **16 approved business tables**, all `STRICT`:

Users, LoginSessions, SystemSettings, Units, Categories, Products, ProductCategories, Bins, CountTasks, CountResults, InventoryTransactions, InventoryTransactionLines, SerializedItems, TransactionLineItems, ActivityLogs, Alerts.

Next to them exist only **framework technical tables** with no business data: `__EFMigrationsHistory` (applied migrations), `__EFMigrationsLock` (EF Core's migration lock on SQLite) and `sqlite_sequence` (SQLite's AUTOINCREMENT counters). `V2SchemaTests` asserts the exact 16 names and that every other table is one of these three.

- Keys: `INTEGER PRIMARY KEY AUTOINCREMENT` (64-bit, never reused).
- Quantities, capacities and thresholds: micro-units (value × 1 000 000) in 64-bit `INTEGER` (`…Micros` columns), checked against ±999 999 999 999.999 999; thresholds multiples of 0.01 %.
- JSON TEXT: user roles, historical SKUs/barcodes/bin codes/serial numbers, log subjects and changes, alert trigger snapshots.
- Unique normalized current codes (username, unit, sibling category, SKU, barcode, bin position incl. archived, serial number), one open session per account, one open task per bin (or open investigation), one open capacity alert per bin, one open discrepancy alert per task, one frozen bin per task.
- Triggers:
  - append-only `InventoryTransactions`, `InventoryTransactionLines`, `TransactionLineItems`, `CountResults`, `ActivityLogs`;
  - no delete on accounts, sessions, settings, products, bins, items, tasks, alerts;
  - fixed distinct role values; a session's role must be assigned; session identity columns fixed and ended sessions final;
  - alerts: the only allowed update is open → resolved (`Status`, `ResolvedAt`, `ResolutionNote`, `Version`); type, bin, product, task, result (NULL-safe), message, snapshot and creation time never change;
  - creation metadata never changes: `Products.CreatedById/CreatedAt/CreationRequestKey/CreationRequestDigest`, `CountTasks.Number/BinId/BinCode/CreatorId/CreatorName/CreatorRole/StartedAt/CreationRequestKey/CreationRequestDigest`;
  - preset unit kept; ASCII-case backstop against reuse of historical codes.
- Technical columns (no extra tables): `Version` on mutable rows; number counters in `SystemSettings`; `RequestKey`/`RequestDigest` on `InventoryTransactions`; creator + `CreationRequestKey` + `CreationRequestDigest` on `Products` and `CountTasks`.

### Create retries (fields only in B1)

B1 adds **only the columns, unique indexes and immutability triggers** for safe `POST /products` and `POST /counts` retries; **neither endpoint exists yet**. B2 (products) and B4 (count start) must implement, inside the creating write transaction: look up the row by (actor, `Idempotency-Key`); if found, compare the stored digest with the SHA-256 of the canonical request body and either return the existing target (same body, `Idempotent-Replayed: true`, nothing created or frozen again) or `409 IDEMPOTENCY_CONFLICT`; otherwise create the row with key and digest. Digests never include credentials. See contracts/v2/README.md §1.2.

### Changing the schema

`20261019230000_InitialV2Schema` runs the SQL in `V2/Migrations/InitialV2SchemaSql.cs`, which is **frozen** from B1 acceptance on: it references nothing else in the project and must never be edited, because databases that applied it record only its ID. Every later change is a new migration class (`[DbContext(typeof(WmsV2DbContext))]`, later timestamp) with its own SQL. `V2Schema.cs` holds names for the model and the tests only.

The provider project now has two contexts. Tooling must always name one:

```powershell
dotnet tool run dotnet-ef migrations list --project src/Wms.Persistence.Sqlite --context WmsDbContext     # v1
dotnet tool run dotnet-ef migrations list --project src/Wms.Persistence.Sqlite --context WmsV2DbContext   # v2
```

v2 migrations are hand-written: the EF model maps only the tables in use, so `migrations add` for `WmsV2DbContext` would not produce a correct diff. For that reason `PendingModelChangesWarning` is ignored for the v2 context only; `V2SchemaTests` covers the schema.

## Identity

- Token: 32 random bytes, base64url (43 chars); only its SHA-256 hex is stored (`LoginSessions.TokenHash`). Sent as `Authorization: Bearer`.
- Every request re-reads session + account: logged out / revoked → `SESSION_REVOKED`; past `ExpiresAt` → `SESSION_EXPIRED`; account disabled or current role no longer assigned → `SESSION_REVOKED`; unknown → `UNAUTHENTICATED`.
- Login: the password is verified against an account snapshot; then, **inside the single `BEGIN IMMEDIATE` write transaction**, the store re-reads the account and requires the same `Version`, `Enabled` and the role still assigned. Only then does it revoke the open sessions (`replacedByNewLogin`), insert the new session and append the log. A password reset or any account edit committed in between bumps `Version`, so nothing is written and the client gets `409 CONCURRENCY_CONFLICT`; a retry verifies against the current hash. A partial unique index enforces one open session.
- Role switch re-reads the session and account inside its write transaction (open session, enabled account, role assigned); logout ends an open session without re-checking the account (ending is always safe).
- Account management (B2) must change `Users` and revoke the account's open sessions in the same write transaction, and bump `Users.Version` on every change.
- Write conflicts (lock timeout, version or unique-index race) are mapped by the use case or, centrally, by `V2ExceptionHandler` to `409 CONCURRENCY_CONFLICT`; never 500.
- Passwords: the existing PBKDF2-HMAC-SHA256 hasher (600 000 iterations, random salt). Unknown accounts are verified against an unusable hash (same cost).
- Logs: successful/failed login, role switch (success and failure), logout. Failed logins store a masked login name and no account; no password, token or hash is logged or returned (the token appears only in the login response).
- Permissions follow the current role only (contracts/v2/README.md §1.1). `sessionHours` applies to new logins only.

## Not in B1

Users/settings management, catalog (including product create and its retry handling), bins, inventory, serial items, counts (including start and its retry handling), alerts, activity-log and dashboard endpoints: B2-B5. Their tables exist; their endpoints answer 404.

## Tests

```powershell
dotnet build Wms.sln
dotnet test tests/Wms.Persistence.Sqlite.Tests --filter "FullyQualifiedName~V2"
dotnet test tests/Wms.Api.Tests --filter "FullyQualifiedName~V2"
dotnet test Wms.sln   # v1 regression
```
