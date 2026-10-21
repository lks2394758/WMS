# Architecture notes

## Projects and dependency direction

```text
Wms.Api ─────> Wms.Application ─────> Wms.Domain
Wms.Worker ──> Wms.Application ─────> Wms.Domain
                 │
                 ├──> Wms.Contracts
                 └──> Wms.Persistence.Abstractions ──> Wms.Domain

Wms.Persistence.Sqlite ──> Wms.Persistence.Abstractions, Wms.Domain   (demonstration provider)
After M4: Wms.Persistence.<Production> ──> Abstractions / Domain
          Wms.Api and Wms.Worker reference a provider only to register it.
```

`tests/Wms.Application.Tests/ArchitectureTests.cs` fails the build if a project file or compiled assembly breaks this direction.

## Decisions taken while implementing M1

| Topic | Decision | Reason |
| --- | --- | --- |
| Persistence.Abstractions → Domain | Reference added | The guide's ports (`IInventoryLockedStore`) use domain types such as `InventoryBalance` |
| Quantities | `int` units | Lots and fractional units are out of scope; changing later breaks v1 |
| Business failures | `Result` / `Result<T>` with `DomainError(Code, Message)` | Codes map one-to-one to API problem codes; no exceptions for expected outcomes |
| Balance mutation | `internal` on `InventoryBalance`; public entry point `InventoryOperations` | Active-state, same-location and stock rules cannot be bypassed |
| Ledger line | One line per product with From/To before-after snapshots | A movement records both sides; a count records its location on the To side and the counted quantity |
| Extra error codes | `LOCATION_INACTIVE`, `NOT_FOUND` | The guide has `PRODUCT_INACTIVE` but no location equivalent, and no code for unknown routes |
| JSON | `WmsJson` options shared by all hosts; UTC written with `Z` | One serializer configuration for API, Worker and tests |
| Solution format | Classic `Wms.sln` (`dotnet new sln --format sln`) | .NET 10 defaults to `.slnx`; the guide's commands reference `Wms.sln` |

## Decisions taken while implementing M2

| Topic | Decision | Reason |
| --- | --- | --- |
| In-memory provider | `InMemoryWmsStore` in `Wms.Application/Persistence/InMemory`, one process-wide writer lock | No provider project exists before M4; it must not be mistaken for proof of database locking |
| Hosting before M4 | `Wms.Api` refuses to start outside `Development` | The in-memory store loses committed transactions on restart (AGENTS.md rule 5) |
| Development data | `DevelopmentData` seeds a fixed catalog and one user per role; the password comes from user-secrets | No password or key is committed (AGENTS.md rule 6) |
| Access token | HS256 JWT, claims `sub`, `name`, `role`; permissions derive from the role | One fixed role → permission matrix (`RolePermissions`) backs both policies and `/auth/me` |
| Movements | Policy `Inventory.Receive` | The guide's eight policies have no movement permission; put-away is receiving-side work |

## Decisions taken while implementing M3

| Topic | Decision | Reason |
| --- | --- | --- |
| OpenAPI contract | `contracts/openapi/wms.v1.json` is compared semantically with `GET /openapi/v1.json` by `OpenApiContractTests`; only login and refresh are public | Client teams build against the committed file; drift fails the build |
| Readiness | `/health/ready` runs the checks tagged `ready`: `persistence` (catalog read through `CatalogQueries`) and `jwt-signing` (sign and verify a probe); `/health/live` runs none | The checks go through the registered ports, so the M4 provider is covered without changing the check |
| Request logging | `RequestLoggingMiddleware` logs method, route template, status and duration inside the `CorrelationId` scope | The route template, not the path, keeps identifiers, query strings and credentials out of logs |
| Rate limiting | Built-in fixed window per user or client address on the controllers only (`RateLimitSetup`) | Health probes and the OpenAPI document must never be throttled |

## Decisions taken for the SQLite demonstration provider

| Topic | Decision | Reason |
| --- | --- | --- |
| Provider project | `Wms.Persistence.Sqlite` references only Domain and Persistence.Abstractions; `Wms.Api` references it only in `Persistence/PersistenceSetup.cs` | Application and Domain stay provider-free; `ArchitectureTests` checks project files and the compiled assembly |
| Provider selection | `Persistence:Provider` = `Sqlite` (default) or `InMemory` (tests, Development only); an unknown value fails at startup. This replaces the M2 rule that the API refuses to start outside Development: it now applies to `InMemory` only, and outside Development `Jwt:SigningKey` stays mandatory | A free, server-less, persistent prototype; the in-memory store stays as the explicit test option |
| File location | `Persistence:Sqlite:DataSource`, default `App_Data/wms-demo.db` resolved against the content root; directory created at startup | Stable location for `dotnet run` and for a published folder; git-ignored with its WAL/SHM files |
| Schema | Persistence rows (not domain types) mapped with EF Core 10; versioned migrations in `src/Wms.Persistence.Sqlite/Migrations`, applied by `MigrateAsync` at startup; no `EnsureCreated` | Later schema changes stay migrations; domain objects are rebuilt through their factories, so stored data is re-validated |
| Constraints | Check constraints for non-negative stock, capacity, transaction type, role and sequence range; unique normalized SKU and username, unique transaction number, unique `(warehouse, code)`; foreign keys; triggers make the ledger and audit tables append-only | The database rejects bad data even when the application is bypassed |
| Write transaction | One `BEGIN IMMEDIATE` transaction per inventory command, after an in-process writer queue; balances, ledger, audit, idempotency record and sequence are saved by one `SaveChanges` and one `COMMIT` | SQLite has one writer per file; taking the lock up front means a transaction never fails half-way on a lock upgrade |
| Concurrency token | `InventoryBalances.Version`, incremented by the application on every write and checked by EF Core | Defence in depth today; the same column carries optimistic checks for a later provider |
| Lock timeout | `Persistence:Sqlite:LockTimeoutSeconds` (10 s); expiry or a token mismatch throws `PersistenceConflictException` (Persistence.Abstractions), which `InventoryCommandService` maps to `CONCURRENCY_CONFLICT` | Nothing is committed, so the client may retry with the same `Idempotency-Key` |
| Timestamps | `DateTimeOffset` stored as UTC ticks (`INTEGER`) | Exact round trip; SQL comparison and ordering work (expiry clean-up) |
| Seeding | `DevelopmentData.SeedAsync` through the `ISeedStore` port: insert-missing by ID or natural key, never update; a password is applied only to seeded users still holding the unusable hash | Restarts never reset balances, edits or passwords; first-run and later behaviour is explicit |
| Journal mode | WAL, set at startup | Readers never wait for the writer |

These decisions prove behaviour **on SQLite only**. They are not evidence for the production database (AGENTS.md rule 4).

## Open decisions (before M4/M5)

- Production database: SQL Server or PostgreSQL. Blocks M4 and every concurrency claim.
- Outbox dispatcher host: `IHubContext` only reaches a hub in the same process. Either run the dispatcher inside `Wms.Api` (IIS AlwaysRunning + preload), or have `Wms.Worker` post events to an internal API endpoint.
- Business date for transaction numbers: warehouse local time or UTC.
