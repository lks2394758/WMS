# Rules for agents and engineers

Read this file, `README.md`, the CI configuration and `docs/` before changing code (guide §13.1).

## Working rules

1. Implement one coherent step at a time; run the focused tests before moving on.
2. Follow the milestone order: M1 §2–5 → M2 §6–7 → M3 §8 → M4 §9 → M5 §10 → M6 §11–12. No mobile integration before M4 passes.
3. Never install a database, IIS feature, certificate or Windows component without explicit user/IT approval.
4. Never claim concurrency safety until the integration tests pass against the chosen production database. The in-memory provider proves nothing about locking.
5. The committed database transaction is the only source of inventory truth. Never use client state, SignalR or a memory cache as final truth.
6. Never print, commit or request passwords, JWTs, private keys, certificates or production connection strings.
7. Breaking API change: update versioning, `contracts/changelog.md`, migration notes and contract tests first. Breaking changes go to `/api/v2`.
8. Future ERP/OMS integration runs asynchronously after the inventory transaction commits; an external timeout must never affect local warehouse correctness.

## Code rules

- Dependency direction (enforced by `ArchitectureTests`): Domain → nothing; Contracts → nothing; Persistence.Abstractions → Domain; Application → Domain, Contracts, Persistence.Abstractions; Api/Worker → Application, Contracts (+ Provider after M4).
- Every package version lives in `Directory.Packages.props`. Never put `Version=` on a `PackageReference`.
- Nullable reference types are on and warnings are errors (`Directory.Build.props`).
- Domain business failures return `Result` with a stable error code; exceptions are for programming errors only.
- Inventory balances change only through `InventoryOperations`; ledger types have no setters.
- Contracts are transport-only records: no domain, EF or provider types. Every request/response type has an example in `contracts/examples`, and the contract tests round-trip it.
- Times are UTC and serialize as `2026-10-10T18:00:00Z` (use `WmsJson`).
- No .NET Framework, `System.Web`, Web Forms, or raw SQL in controllers.
