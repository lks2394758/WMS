# Architecture notes

## Projects and dependency direction

```text
Wms.Api ─────> Wms.Application ─────> Wms.Domain
Wms.Worker ──> Wms.Application ─────> Wms.Domain
                 │
                 ├──> Wms.Contracts
                 └──> Wms.Persistence.Abstractions ──> Wms.Domain

After M4: Wms.Persistence.Provider ──> Abstractions / Application / Domain
          Wms.Api and Wms.Worker reference the provider only to register it.
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

## Open decisions (before M4/M5)

- Production database: SQL Server or PostgreSQL. Blocks M4 and every concurrency claim.
- Outbox dispatcher host: `IHubContext` only reaches a hub in the same process. Either run the dispatcher inside `Wms.Api` (IIS AlwaysRunning + preload), or have `Wms.Worker` post events to an internal API endpoint.
- Business date for transaction numbers: warehouse local time or UTC.
