# WMS Backend

Intranet backend for a small, single-warehouse inventory system: .NET 10, ASP.NET Core, hosted on Windows (IIS + Windows Service). Android, iOS and a future admin UI use it only through the versioned REST API (OpenAPI v1) and SignalR change events; they never touch the database.

## Status

V2 update (2026-10-20): B0–B5 SQLite demonstration prototype automated acceptance passed: backend 545, real business/API/browser 62, real Kestrel failure audit 7. Manual business acceptance and production deployment are not claimed. See [docs/v2-prototype-acceptance.zh-CN.md](docs/v2-prototype-acceptance.zh-CN.md) for results, limitations and v2 startup.

**Milestone M1 (guide §2–5) complete.** The solution has locked baseline dependencies, code-quality rules, an OpenAPI skeleton, the standard error contract, the inventory domain rules and their unit tests.

**Milestone M2 (guide §6–7):** persistence ports, the in-memory provider, use cases, JWT authentication, the permission policies and the v1 controllers are wired.

**Milestone M3 (guide §8) accepted:** committed OpenAPI contract with a drift test, field validation problems, correlation IDs, rate limiting, request completion logging and real readiness checks. See [docs/m3-acceptance.md](docs/m3-acceptance.md).

**SQLite demonstration persistence (approved prototype phase):** by default the API stores everything in one local SQLite file, so data survives restarts and the prototype runs without a database server. See [docs/sqlite-demo-acceptance.md](docs/sqlite-demo-acceptance.md).

SQLite is a **demonstration provider**, not the production database. Production concurrency safety (SQL Server or PostgreSQL) has **not** been validated; that happens only at the database gate (M4, guide §9).

## Prerequisites

- .NET SDK 10.0.401 (pinned in `global.json`, patch roll-forward allowed)
- Visual Studio 2022 with the *ASP.NET and web development* workload, or any editor plus the CLI
- Git

IIS, the Hosting Bundle and a database server are **not** needed: the SQLite engine ships inside the NuGet package. `dotnet-ef` is needed only to add migrations, and only as the repository-local tool (`dotnet tool restore`), never as a global tool.

## Build and test

```powershell
dotnet restore Wms.sln
dotnet format whitespace Wms.sln --verify-no-changes
dotnet build Wms.sln --no-restore
dotnet test Wms.sln --no-build
```

or run `./scripts/verify.ps1`, which runs the same four steps and stops at the first failure. The SQLite tests create their database files under the system temp folder and delete them afterwards.

## Demo startup (SQLite)

```powershell
# Once per machine: development secrets, stored in your user profile and never in the repository.
dotnet user-secrets --project src/Wms.Api set "DevelopmentSeed:Password" "<choose a local password>"
dotnet user-secrets --project src/Wms.Api set "Jwt:SigningKey" ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))

# Every time:
dotnet run --project src/Wms.Api --launch-profile http
```

Then open `http://localhost:5056/health/ready` (answers `Healthy`) and `http://localhost:5056/openapi/v1.json`. Sign in with `POST /api/v1/auth/login` as `admin`, `supervisor`, `receiver` or `shipper`, using the password you set.

**Browser test UI:** with the API running, start `cd prototypes/wms-test-ui; npm start` (Node 22, no packages) and open `http://localhost:4173`. Its same-origin `/api` proxy talks to `WMS_API_BASE_URL` (default `http://127.0.0.1:5056`). Alternatively, `./prototypes/wms-test-ui/start-demo.ps1` starts both. See [prototypes/wms-test-ui/README.md](prototypes/wms-test-ui/README.md) and [docs/test-ui-api-acceptance.md](docs/test-ui-api-acceptance.md).

- **Database file:** `src/Wms.Api/App_Data/wms-demo.db`, resolved against the content root (a published app uses `<publish folder>/App_Data/wms-demo.db`). The directory is created on first start. Git ignores the file and its `-wal` and `-shm` companions.
- **Startup order:** create the directory, apply pending EF Core migrations (`MigrateAsync`), switch the file to WAL, then seed. Migration is repeatable: it leaves an up-to-date file unchanged.
- **Seeding:** the first start inserts 3 locations, 2 products with barcodes and one user per role. It seeds no stock. Later starts insert only missing rows and never touch balances, the ledger or existing rows, so changed data and passwords survive a restart. One intentional exception: users created while `DevelopmentSeed:Password` was unset cannot sign in, and they receive the password once you set it. `DevelopmentSeed:Enabled=false` turns seeding off.
- **Secrets:** `Jwt:SigningKey` and `DevelopmentSeed:Password` come from user-secrets or the environment (`Jwt__SigningKey`, `DevelopmentSeed__Password`). Without a signing key, Development uses a random key per process, so tokens stop working after a restart. Outside Development the key is mandatory.
- **Reset the demo:** stop the API, delete `App_Data/wms-demo.db*`, start again.
- **Backup:** stop the API and copy `wms-demo.db`; a clean stop merges the `-wal` file back. While the API runs, use SQLite's online backup instead, for example `sqlite3 wms-demo.db ".backup 'backups/wms-demo.bak'"` (git ignores `backups/`). Never copy only the `.db` file of a running instance.

### Persistence configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Persistence:Provider` | `Sqlite` | `Sqlite` (demonstration file) or `InMemory` (tests; Development only; data is lost on restart) |
| `Persistence:Sqlite:DataSource` | `App_Data/wms-demo.db` | File path; a relative path resolves against the content root |
| `Persistence:Sqlite:LockTimeoutSeconds` | `10` | How long a write waits for the single writer lock before `409 CONCURRENCY_CONFLICT`. Nothing is stored; retry with the same `Idempotency-Key` |

### Limitations of the SQLite demonstration provider

- The whole file has one writer at a time. Writes queue in the process, then on SQLite's file lock (`BEGIN IMMEDIATE`). That is enough for a demo; it is not a throughput or multi-server design.
- Keep the file on a local disk. Never put it on a network share, and never run API instances on several machines against it.
- Its tests prove atomic commit, rollback, constraints and safe rejection **on SQLite only**. They say nothing about SQL Server or PostgreSQL row locking (AGENTS.md rule 4).
- Only inventory commands map a lock timeout to `CONCURRENCY_CONFLICT`. An auth or audit write that exceeds the lock timeout fails with 500.

### Switching to SQL Server or PostgreSQL later (M4)

The use cases depend only on the ports in `Wms.Persistence.Abstractions`, so a new provider changes no Application, Domain or controller code:

1. Add a provider project, for example `Wms.Persistence.SqlServer`, that references only Domain and Persistence.Abstractions, implements the same ports and registers itself like `AddWmsSqlitePersistence`. Add it to `ArchitectureTests`.
2. Give it its **own** migrations, generated for that provider (`dotnet tool run dotnet-ef migrations add Initial --project src/Wms.Persistence.SqlServer`). The SQLite migrations are SQLite-specific (types, triggers) and cannot be reused.
3. Lock balance rows inside the write transaction in location-then-product order (`UPDLOCK, ROWLOCK` or `SELECT ... FOR UPDATE`), and keep the `Version` concurrency token.
4. Add the provider name to `PersistenceSetup`. Keep its connection string in a secret store or environment variable, never in a committed file.
5. Move existing data with an explicit one-off data migration: export from the SQLite file and import in dependency order (users, locations, products, barcodes, balances, ledger, audit, sequences). Refresh tokens and expired idempotency keys may be dropped. Afterwards, compare row counts and per-product stock totals.
6. Run the provider-neutral tests, plus concurrency and rollback integration tests against the target database itself. Only then may concurrency safety be claimed (AGENTS.md rule 4).

## Layout

```text
src/
  Wms.Domain                     pure domain: entities, value objects, rules (references nothing)
  Wms.Contracts                  transport DTOs, error codes, event types, JSON settings
  Wms.Persistence.Abstractions   persistence ports (M2)
  Wms.Persistence.Sqlite         SQLite demonstration provider: EF Core model, migrations, write scope
  Wms.Application                use cases and command pipeline (M2)
  Wms.Api                        ASP.NET Core host: REST, OpenAPI, problem details, health
  Wms.Worker                     Windows Service host: background jobs
tests/
  Wms.Domain.Tests               domain rules, including the 8 mandatory tests of guide §5.3
  Wms.Application.Tests          architecture tests enforcing the dependency direction
  Wms.ContractTests              DTO round-trips against contracts/examples, contract shape
  Wms.Api.Tests                  HTTP tests: auth, policies, validation, OpenAPI drift, correlation, rate limits, logging, health (in-memory); host restart on SQLite
  Wms.Persistence.Sqlite.Tests   on-disk SQLite: migrations, seeding, restart, idempotency, atomicity, constraints, concurrency
contracts/                       OpenAPI, event contract, JSON examples, changelog
docs/                            API conventions, architecture notes
```

## Documentation

- [AGENTS.md](AGENTS.md): rules for AI agents and engineers changing this repository
- [docs/api-conventions.md](docs/api-conventions.md)
- [docs/architecture.md](docs/architecture.md)
- [docs/sqlite-demo-acceptance.md](docs/sqlite-demo-acceptance.md)
- [contracts/changelog.md](contracts/changelog.md)
- [contracts/v2/README.md](contracts/v2/README.md), [docs/v2-b1-identity.md](docs/v2-b1-identity.md), [docs/v2-b2a-catalog.md](docs/v2-b2a-catalog.md), [docs/v2-b2b-admin.md](docs/v2-b2b-admin.md), [docs/v2-b3-inventory.md](docs/v2-b3-inventory.md), [docs/v2-b3-serials.md](docs/v2-b3-serials.md), [docs/v2-b4-counts.md](docs/v2-b4-counts.md): API v2 (opt-in process mode `V2:Enabled=true`, separate database `App_Data/wms-v2-demo.db`; accepted through B5a dashboard/alerts/activity queries; framework failure audit and final automated prototype acceptance also passed; see `docs/v2-prototype-acceptance.zh-CN.md`)
- [contracts/events/inventory-events.v1.md](contracts/events/inventory-events.v1.md)

## Next milestone

M4 (guide §9): the database gate. It needs the production database decision (docs/architecture.md, open decisions) and IT approval before anything is installed.
