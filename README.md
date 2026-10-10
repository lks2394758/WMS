# WMS Backend

Intranet backend for a small, single-warehouse inventory system: .NET 10, ASP.NET Core, hosted on Windows (IIS + Windows Service). Android, iOS and a future admin UI use it only through the versioned REST API (OpenAPI v1) and SignalR change events; they never touch the database.

## Status

**Milestone M1 (guide §2–5) complete.** The solution has locked baseline dependencies, code-quality rules, an OpenAPI skeleton, the standard error contract, the inventory domain rules and their unit tests.

No database is connected, and production concurrency safety has **not** been validated. That happens only at the database gate (M4, guide §9).

## Prerequisites

- .NET SDK 10.0.401 (pinned in `global.json`, patch roll-forward allowed)
- Visual Studio 2022 with the *ASP.NET and web development* workload, or any editor plus the CLI
- Git

IIS, the Hosting Bundle, a database and `dotnet-ef` are **not** needed until later milestones.

## Build and test

```powershell
dotnet restore Wms.sln
dotnet format whitespace Wms.sln --verify-no-changes
dotnet build Wms.sln --no-restore
dotnet test Wms.sln --no-build
```

or run `./scripts/verify.ps1`, which runs the same four steps and stops at the first failure.

Run the API locally: `dotnet run --project src/Wms.Api`, then open `/openapi/v1.json` and `/health/live`.

## Layout

```text
src/
  Wms.Domain                     pure domain: entities, value objects, rules (references nothing)
  Wms.Contracts                  transport DTOs, error codes, event types, JSON settings
  Wms.Persistence.Abstractions   persistence ports (M2)
  Wms.Application                use cases and command pipeline (M2)
  Wms.Api                        ASP.NET Core host: REST, OpenAPI, problem details, health
  Wms.Worker                     Windows Service host: background jobs
tests/
  Wms.Domain.Tests               domain rules, including the 8 mandatory tests of guide §5.3
  Wms.Application.Tests          architecture tests enforcing the dependency direction
  Wms.ContractTests              DTO round-trips against contracts/examples, contract shape
contracts/                       OpenAPI, event contract, JSON examples, changelog
docs/                            API conventions, architecture notes
```

## Documentation

- [AGENTS.md](AGENTS.md): rules for AI agents and engineers changing this repository
- [docs/api-conventions.md](docs/api-conventions.md)
- [docs/architecture.md](docs/architecture.md)
- [contracts/changelog.md](contracts/changelog.md)
- [contracts/events/inventory-events.v1.md](contracts/events/inventory-events.v1.md)

## Next milestone

M2 (guide §6–7): persistence ports, the in-memory test provider, use cases, JWT authentication, the eight authorization policies, controllers and the generated OpenAPI v1 contract.
