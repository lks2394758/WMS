# API v2 — B3b: serialized items and serial number correction

Status: **independently accepted by Codex on 2026-10-20** after Claude's implementation and review correction, build, full backend tests and real UI/API checks. SQLite evidence only (AGENTS.md rule 4). Endpoints, DTOs and codes: `contracts/v2/README.md` §2, §8. No schema change (frozen initial migration, 16 business tables); no new table.

## Layers

| Layer | Files |
| --- | --- |
| Ports | `Wms.Persistence.Abstractions/V2/InventoryPortsV2.cs`: `IV2SerialReader`; `IV2SerialWriteOperations` (part of the single `IV2CatalogWriteScope`) |
| Rules | `Wms.Application/V2/Inventory/V2SerialService.cs` (reuses `V2CatalogWriter`, `V2Text`, `V2Paging`) |
| SQLite | `Wms.Persistence.Sqlite/V2/V2SqliteSerials.cs` |
| HTTP | `Wms.Api/V2/V2SerializedItemsController.cs` (thin), registered only in `V2ControllerFeatureProvider` |

## Reads (signed in)

- `GET /api/v2/serialized-items`: exact `productId`, `binId`, `inStock` filters in SQL, ascending ID; `search` matches the current number and every corrected (historical) number as a substring by Unicode invariant upper case, applied before the page is cut. A row found only by an old number carries it in `matchedHistoricalSerialNumber`. Items of archived products stay listed.
- `GET /api/v2/serialized-items/{id}`: the item, its product's current name and SKU (archived products included) and current bin; `history` = every ledger line that touched it, from the immutable transaction, line and item-link snapshots (number, bin codes, actor name/role and time as they were then); `corrections` = the immutable `serialCorrect` activity entries naming the item (old and new number, reason, actor name, actual role, time). Nothing is inferred from the current number.

## Correction

`POST /api/v2/serialized-items/{id}/correct {serialNumber, reason, version}` — permission `SerialNumbers.Correct` of the **current** role (administrator, supervisor).

1. Request checks (logged as rejections with the verified identity): number 1-128 characters, no control characters, no leading/trailing spaces; reason non-blank, ≤ 2000; `version` ≥ 1 (the current version is required).
2. One transaction on the shared v2 writer; clock read after the lock; session, current role and permission re-checked inside.
3. The stored `version` must match first (contract §1): a stale version is `409 VERSION_CONFLICT` whatever the number, the exact current number included.
4. With the current version, the same number as the current one (exactly) is a **no-op**: `200` with the item as it is, no log, no history entry, no version change.
5. Otherwise the new number must not equal (Unicode case-insensitive) any current or historical number of any item, including this item's own old numbers and a case variant of its current number (`409 ALREADY_EXISTS`, `errors.serialNumber`, owner in `subjects`).
6. The old current number is appended to `historicalSerialNumbers` once and stays reserved forever; the version is bumped. The item ID, product, bin, stock flag, bin quantities and capacity, count tasks and every ledger/alert/count snapshot are untouched.
7. One immutable activity entry (same transaction): before/after number, reason, actor, actual role, time; subjects item (new number), product, bin when in stock. Failures roll back and are logged separately.

Concurrent corrections are serialized: two different numbers from one version → one succeeds, the other `VERSION_CONFLICT`; the same number twice from one version → likewise one change, one success log entry and one history entry, the other `VERSION_CONFLICT` (its version is stale once the first commits).

After a correction, inventory operations refuse the old number with `SERIAL_NUMBER_SUPERSEDED` naming the current number; the new number moves, ships and returns the same physical item (same ID); earlier ledger lines keep the old number.

## Known limitations

- Requests rejected before the use case runs (model binding/JSON 400, policy 403, 401, 429, 413) are not in the activity log; deferred to the B5 cross-cutting audit.
- The uniqueness check reads every serial number once per correction; corrections are read by matching the stored subject JSON. Fine for the demonstration size; a production provider should index normalized historical numbers and log subjects.
- A case-only change of a number (e.g. `sc-1` → `SC-1`) is refused as the same identity.
- B4 counts and B5 alerts/activity/dashboard endpoints are not implemented.

## Independent acceptance (Codex, 2026-10-20)

- Build: zero warnings/errors. V2 whitespace verification passed.
- Full backend **521 passed**, zero failed/skipped: Domain 77, Application 12, Contracts 205, API 88, SQLite 139.
- Real serial/API/browser **9 passed**: exact filters, persisted history, correction evidence without stock changes, old-number refusal/current-number subjects, current-version no-op/stale-version rejection, globally reserved numbers, physical return ID continuity, Unicode search before paging, read-only clerk identity, actual correction form and in-stock picker shipment. Screenshot `artifacts/v2-real-ui/serials.png` visually checked.
- The first independent run had 8 passes and 1 failure: stale version plus the already-current number answered 200. Claude moved version validation before no-op and corrected the SQLite/HTTP concurrency assertions and documentation. All 9 real checks then passed; no failing assertion was dropped or weakened.
- Full real-API regression: **47 passed** (auth 7, catalog 6, bins/admin 10, inventory 15, serials 9). Frontend **51 passed**, browser contract fixture **18 passed**. Old-database sentinels remained unchanged; all owned temporary servers/profiles/browsers were cleaned up. No default user database or account was reset.
- Only temporary SQLite has been verified, not SQL Server/PostgreSQL production concurrency. B4/B5 and framework-level failure auditing remain unfinished.
- Claude's final usage report: no trustworthy remaining-context or account-quota meter is available in its file-only task; remaining token counts cannot be reported accurately.

Re-run backend checks:

```powershell
dotnet build Wms.sln --no-restore
dotnet test tests/Wms.Persistence.Sqlite.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test tests/Wms.Api.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test Wms.sln --no-build
```
