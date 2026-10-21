# SQLite demonstration persistence: acceptance

Date: 2026-10-14. Verdict: **PASS for the SQLite demonstration provider.**

**Scope.** This is a prototype database gate, approved by the user. It shows that the API persists catalog, users, refresh tokens, balances, ledger, audit trail, sequences and idempotency records in one local SQLite file, commits each inventory command atomically, and rejects concurrent writers safely **on SQLite**. It is **not** the M4 database gate. It proves nothing about SQL Server or PostgreSQL locking or concurrency (AGENTS.md rule 4).

## How it was run

The commands ran in the repository itself with SDK 10.0.401 (`global.json` unchanged). Logs, TRX files and the scripts are under the git-ignored `artifacts/sqlite-demo/`:

| Path | Content |
| --- | --- |
| `artifacts/sqlite-demo/baseline/` | Build and test before any change: 0 warnings, 0 errors, all tests passed |
| `artifacts/sqlite-demo/final/` | Final `restore`, `build --no-incremental` and `test` logs, `trx/*.trx`, `summary.txt`, format logs |
| `artifacts/sqlite-demo/work/` | Intermediate runs, including three repeated runs of the SQLite test project |
| `artifacts/sqlite-demo/runtime-smoke/` | Two real `dotnet run` processes on one database file (see below) |
| `artifacts/sqlite-demo/verify-sqlite-demo.ps1` | Re-runs the final sequence and writes `summary.txt` |

| Step | Result |
| --- | --- |
| `dotnet restore Wms.sln` | exit 0 |
| `dotnet build Wms.sln --no-restore --no-incremental` (warnings are errors) | exit 0, **0 warnings, 0 errors** |
| `dotnet test Wms.sln --no-build --logger trx --blame-hang --blame-hang-timeout 180s` | exit 0 in 9 s, no hang |
| `dotnet format whitespace --verify-no-changes --include <every file created here, plus ArchitectureTests.cs>` | exit 0 |
| `dotnet format whitespace Wms.sln --verify-no-changes` | exit 2: only `ENDOFLINE`, in 48 uncommitted M2/M3 files that were already LF (see "Remaining issues") |

| Test project | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| Wms.Domain.Tests | 60 | 0 | 0 |
| Wms.Application.Tests | 12 | 0 | 0 |
| Wms.ContractTests | 25 | 0 | 0 |
| Wms.Api.Tests | 57 | 0 | 0 |
| Wms.Persistence.Sqlite.Tests (new) | 35 | 0 | 0 |
| **Total** | **189** | **0** | **0** |

The baseline was 150 tests. The new ones: 35 SQLite tests, 2 HTTP tests on SQLite and 2 architecture tests. `Wms.Persistence.Sqlite.Tests` also passed three repeated runs in a row (35/35 each). `OpenApiContractTests` passed: the runtime document still matches `contracts/openapi/wms.v1.json`, and that file was not changed.

## What the tests prove (on-disk SQLite files in `%TEMP%`)

| Area | Tests |
| --- | --- |
| Startup | The first start creates the missing directory and applies migration `InitialSqliteSchema` (`MigrateAsync`, never `EnsureCreated`). The journal mode is `wal`. There are no pending migrations and no pending model changes. Three hosts starting at once on one file migrate and seed exactly once |
| Seeding | The first run inserts 11 records (3 locations, 2 products, 2 barcodes, 4 users) and no stock. A restart inserts 0 and keeps balances, edited rows and the original password, even when another password is configured. Users created without a password receive it once it is configured, and only then |
| Restart | A new host on the same file reads the catalog (with barcodes), the balances, the ledger with before/after snapshots, the actor and reference, and the audit actions. The daily sequence continues (`-000002`). Keyset pages of balances keep the location-then-product order |
| Idempotency | After a restart, the same key and body replay the stored response, and a different body gives `IDEMPOTENCY_CONFLICT`. The same key from another user is a new action. An expired key is replaced and runs again. UTC timestamps round-trip exactly |
| Auth | A refresh token issued before a restart rotates after it. Reusing it revokes the whole family and is audited. Only token hashes are stored |
| Atomicity | A business failure on line 2 stores nothing (balances, versions, ledger, audit, idempotency and sequence are identical), and the same key is evaluated again. A database failure at commit (duplicate audit primary key) rolls back every part, releases the writer and does not consume the number. Disposing an uncommitted scope rolls back and releases the writer |
| Constraints | The database rejects a negative balance (check), a duplicate SKU or username in another case (unique index on the normalized value), a duplicate transaction number, and a balance for an unknown product (foreign key). `UPDATE` and `DELETE` on the ledger, ledger lines and audit table are rejected by triggers. A stale `Version` raises `DbUpdateConcurrencyException` |
| Concurrency | 30 concurrent single-unit shipments from 3 hosts (3 separate in-process queues meeting at the SQLite file lock) against 12 units: exactly 12 succeed, 18 get `INSUFFICIENT_STOCK`, the final stock is 0, numbers `000001`–`000012` have no gap or duplicate, and `Version` is 13. 10 concurrent requests with one key commit once (1 original, 9 replays). A writer blocked by another host's open transaction gets `CONCURRENCY_CONFLICT` after the 1 s timeout, stores nothing, and reads are not blocked meanwhile; the same key then succeeds as a first execution. The in-process queue timeout behaves the same. Two concurrent refreshes of one token rotate it exactly once |
| HTTP | `SqliteApiTests`: two `WebApplicationFactory` hosts on one file give readiness 200; the refresh token from host 1 works on host 2; the balance persists; the idempotent replay returns `Idempotent-Replayed: true` with the same number; the conflict changes nothing. An unknown provider fails at startup |
| Architecture | `Wms.Persistence.Sqlite` references only Domain and Persistence.Abstractions. Application references no provider. Api and Worker may reference the provider |

### Runtime smoke test (real processes)

`dotnet run --project src/Wms.Api --no-build --launch-profile http` was started twice against `artifacts/sqlite-demo/runtime-smoke/App_Data/wms-demo.db`. The seed password and the JWT key were random values passed only through the process environment; they were never printed or written:

- Run 1: readiness 200, file created, migration `20261014235123_InitialSqliteSchema` applied, 11 seed records; receipt `RCV-20261015-000001` brought the balance to 11.
- Run 2: "migrations applied none (up to date)", 0 seed records. The run-1 refresh token was accepted, the balance was still 11, and the same `Idempotency-Key` replayed with `Idempotent-Replayed: true` and the same number.
- The directory held `wms-demo.db`, `wms-demo.db-shm` and `wms-demo.db-wal`. A scan of the logs found no password, key or token (EF Core logs SQL with parameter placeholders only). EF command logging is now at `Warning` in `appsettings.json`.

The business date is the UTC date (`20261015` while local time was still 2026-10-14). That is the existing open decision in `docs/architecture.md`.

## Design summary

- `Persistence:Provider` selects `Sqlite` (default) or `InMemory` (explicit test option, Development only) in `src/Wms.Api/Persistence/PersistenceSetup.cs`. Application, Domain and controllers see only the ports.
- The database file is `Persistence:Sqlite:DataSource`, default `App_Data/wms-demo.db`, resolved against the content root. For `dotnet run` that is `src/Wms.Api/App_Data/wms-demo.db`.
- Every write takes an in-process writer slot, then `BEGIN IMMEDIATE`. An inventory command's balances, ledger, audit entry, idempotency record and sequence are written by one `SaveChanges` and one `COMMIT`. Balances still change only through `InventoryOperations`; the provider writes back the resulting quantity.
- A lock timeout (`Persistence:Sqlite:LockTimeoutSeconds`, 10 s) or a concurrency-token mismatch throws `PersistenceConflictException`. `InventoryCommandService` maps it to `409 CONCURRENCY_CONFLICT`, an existing v1 code.
- Migrations are versioned in `src/Wms.Persistence.Sqlite/Migrations`. They were generated with the repository-local tool (`.config/dotnet-tools.json`, `dotnet-ef` 10.0.12) and applied programmatically at startup.

## Changed files

New:

- `.config/dotnet-tools.json`: local `dotnet-ef` 10.0.12 (`dotnet tool restore`; no global tool)
- `src/Wms.Persistence.Abstractions/PersistenceConflictException.cs`, `SeedPorts.cs` (`ISeedStore`, `SeedData`)
- `src/Wms.Persistence.Sqlite/`: `Wms.Persistence.Sqlite.csproj`, `SqliteOptions.cs`, `Rows.cs`, `WmsDbContext.cs`, `RowMapping.cs`, `SqliteWmsStore.cs`, `SqliteWriteScope.cs`, `SqlitePersistence.cs`, `Migrations/20261014235123_InitialSqliteSchema.cs`, `.Designer.cs`, `WmsDbContextModelSnapshot.cs`
- `src/Wms.Api/Persistence/PersistenceSetup.cs`
- `tests/Wms.Persistence.Sqlite.Tests/`: `Wms.Persistence.Sqlite.Tests.csproj`, `SqliteTestHost.cs`, `MigrationAndSeedTests.cs`, `RestartPersistenceTests.cs`, `AtomicityTests.cs`, `ConstraintTests.cs`, `ConcurrencyTests.cs`
- `tests/Wms.Api.Tests/TestEnvironment.cs` (the existing HTTP tests select `InMemory` explicitly), `tests/Wms.Api.Tests/SqliteApiTests.cs`
- `docs/sqlite-demo-acceptance.md`

Modified:

- `Directory.Packages.props`: `Microsoft.EntityFrameworkCore.Sqlite` and `.Design` 10.0.12
- `Wms.sln`: the two new projects
- `.gitignore`: `App_Data/`, `*.db`, `*.db-wal`, `*.db-shm`, `*.db-journal`
- `src/Wms.Api/Program.cs`, `Wms.Api.csproj`, `appsettings.json`: provider selection, startup migration and seed, `Persistence` section, EF command logging at `Warning`
- `src/Wms.Application/Inventory/InventoryCommandService.cs`, `Common/ApplicationErrors.cs`: `PersistenceConflictException` becomes `CONCURRENCY_CONFLICT`
- `src/Wms.Application/Persistence/InMemory/DevelopmentData.cs`, `InMemoryWmsStore.cs`, `DependencyInjection.cs`: provider-neutral, idempotent `SeedAsync` through `ISeedStore`
- `src/Wms.Persistence.Abstractions/PersistenceAbstractionsAssembly.cs` (doc comment)
- `tests/Wms.Application.Tests/ArchitectureTests.cs`, `Wms.Application.Tests.csproj`
- `README.md`, `docs/architecture.md`, `docs/api-conventions.md`, `contracts/changelog.md`

Not changed: `global.json`, `contracts/openapi/wms.v1.json`, `prototypes/`, `tools/`, and all domain code.

## Remaining issues and blockers

- **Production database (M4) is still open.** SQL Server or PostgreSQL needs a decision and IT approval. That provider needs its own migrations, an explicit SQLite-to-target data migration, and concurrency and rollback tests against the target database. Until then, no production concurrency claim may be made.
- **SQLite limits:** one writer per file, local disk only, one machine. Under contention, writes from other processes poll the lock (Microsoft.Data.Sqlite busy retry), which adds latency.
- An auth or audit write that exceeds the lock timeout returns 500, not 409. Only inventory commands map the timeout to `CONCURRENCY_CONFLICT`.
- With `Persistence:Provider=Sqlite`, the API now also starts outside Development; `Jwt:SigningKey` stays mandatory there. This is intended for deploying the prototype, but it was not exercised outside Development here.
- `dotnet format whitespace Wms.sln --verify-no-changes` fails with `ENDOFLINE` on 48 uncommitted M2/M3 files that use LF; `.gitattributes` (`eol=crlf`) normalizes them on commit. Their line endings were preserved deliberately, so the user's changes stay untouched. Every file created in this phase is CRLF and passes.
- No commit was made.
