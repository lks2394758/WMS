# API v2 — B3a: inventory operations and the immutable ledger

Status: **B3a independently accepted by Codex on 2026-10-20** (build, full backend tests, real UI/API checks). SQLite evidence only (AGENTS.md rule 4): the tests prove atomicity and rejection on the SQLite demonstration file, not production database concurrency. Endpoints, DTOs and codes: `contracts/v2/README.md` §1.2, §1.3, §2, §7. B3b query/correction was later resumed and independently accepted; see `docs/v2-b3-serials.md` for the latest 521-test regression and serial acceptance. Serial rules needed by the four operations are part of accepted B3a.

No schema change: the frozen initial migration already holds `InventoryTransactions` (with `RequestKey`/`RequestDigest`), `InventoryTransactionLines`, `TransactionLineItems`, `SerializedItems` and the `SystemSettings` counters; no new business table.

## Layers

| Layer | Files |
| --- | --- |
| Domain (pure) | `Wms.Domain/V2/Inventory/V2InventoryRules.cs`: `V2Micro` (exact micro-units, 128-bit packaging product, precision), `V2BinState` (one bin's working state inside a transaction) |
| Ports | `Wms.Persistence.Abstractions/V2/InventoryPortsV2.cs` (`IV2InventoryReader`; `IV2InventoryWriteOperations`, part of the single `IV2CatalogWriteScope`) |
| Rules | `Wms.Application/V2/Inventory/`: `V2InventoryRequestParser` (request grammar per operation), `V2InventoryService` (the transaction), `V2TransactionQueries`, `V2InventoryMapping`; shared `V2CapacityAlerts`, `V2ProductService.CodeIndex` |
| SQLite | `Wms.Persistence.Sqlite/V2/V2SqliteInventory.cs`, `V2InventoryRows.cs`, mappings in `WmsV2DbContext` |
| HTTP | `Wms.Api/V2/V2InventoryController.cs` (thin), registered only in `V2ControllerFeatureProvider` |

## One request = one transaction

`POST /api/v2/inventory/receipts|shipments|movements|adjustments` (permissions `Inventory.Receive|Ship|Move|Adjust` of the **current** role; `Idempotency-Key` UUID required):

1. Request validation without stored data (`V2InventoryRequestParser`): 1-200 lines; members per operation as in §7.1 ("—" → 400); exact quantity strings (signed only for adjustments), percent strings, ID strings; `source` (receipts) and `destination` (shipments) ≤ 200; `reason` ≤ 2000 and required for adjustments; `countResultId` only for adjustments; packaging members together; serial numbers trimmed, unique in the request (Unicode case-insensitive); `generateSerialCount` 1-10 000 per line and exclusive with manual numbers; **at most 10 000 serial items per request in total, entered plus generated over all lines**, checked while the lines are read (a huge manual list stops at the first item over the limit), before any database work; inline product fields. A rejected request is logged with the verified identity (writer pattern) and changes nothing.
2. The shared v2 writer opens one `BEGIN IMMEDIATE` transaction; the clock is read after the lock; the session, current role and permission are re-checked inside it.
3. Idempotency: the account + key is looked up in `InventoryTransactions`. Same canonical body (SHA-256 of type + bound request) → the stored transaction is returned as it was (`201`, `Idempotent-Replayed: true`; snapshots unchanged even if the product was renamed since). Other body → `409 IDEMPOTENCY_CONFLICT`. Concurrent requests with one key are serialized by the writer: one transaction, the others replay. The unique `(ActorId, RequestKey)` index is the backstop across processes. Survives restarts.
4. Count result link (adjustments): the result must exist and every line must adjust its bin and product.
5. Products: existing ones must exist and not be archived. Inline receipt products (`newProduct`, also for `receivingClerk`, without granting `Catalog.Write`): same field rules as product create, unit by ID or by name (Unicode case-insensitive reuse, else a new unit), SKU/barcode generated from the counters skipping every current and historical code, logged; all rolled back with the request.
6. Lines in order on the accumulated state of each bin (`V2BinState`) and serial item: quantity = entered, or packaging quantity × conversion factor (exact 128-bit product, never rounded; must equal an entered quantity), or the number of serial items; every quantity fits the product's decimal places and the range. One product per bin; empty bins need a capacity (product precision) and start with default thresholds unless overrides are sent (effective low < high); bins with stock reject capacity/overrides; a zero bin keeps product and capacity; another product enters only an empty bin without an unresolved investigation; frozen bins (`BIN_FROZEN`) and archived bins/products (`ARCHIVED`) accept nothing; stock never negative. `BIN_FROZEN` and `INVESTIGATION_UNRESOLVED` name the bin, the product and the count task(s) actually freezing the bin or holding the investigation (`countTask` subjects); no task subject is added when there is none.
7. Serial items (tracked products, 0 decimals): receipts/positive adjustments create new items or bring back the **same** item (same ID) when it is out of stock; in-stock → `SERIAL_ALREADY_IN_STOCK`; another product's → `ALREADY_EXISTS`; generated numbers `SN-000001`… skip every current, historical and entered number. Shipments, movements and negative adjustments (quantity `-count`) need each item in stock in that exact bin for that product (`SERIAL_NOT_IN_BIN`); an item cannot be used twice in one request. A corrected (old) number → `SERIAL_NUMBER_SUPERSEDED` with the current number in `subjects`. A known item of a product whose tracking is now off → `SERIAL_TRACKING_DISABLED`; serial members for untracked products → `SERIAL_NUMBERS_REQUIRED`.
8. Commit in the same transaction: bins (optimistic version), item positions, transaction number `TX-000001…` from the counter, the immutable transaction with product/unit/bin/actor snapshots, before/after of each side, packaging and item links (number as it was then); capacity alerts of the changed bins re-evaluated with the B2b rule (continuing states keep their original snapshot; type changes resolve and open; investigations never touched); one success log entry naming the transaction, products, bins, items, created products/units, count result and changed alerts.

Any failure rolls all of this back (inline units/products, items, counters, alerts included) and is then logged separately.

## History

`GET /api/v2/inventory/transactions` (newest first): `type`, `from` (inclusive), `to` (exclusive), `productId`, `binId` (either side), `actorId` in SQL; `search` (number, source, destination, reason, line product names/SKUs) by Unicode invariant upper case before the page is cut. `GET /api/v2/inventory/transactions/{id}`: the stored transaction. Signed-in users. No images or credentials.

## Known limitations

- Requests rejected before the use case runs (model binding/JSON 400, policy 403, 401, 429, 413) are not in the activity log; deferred to the B5 cross-cutting audit.
- B4 counts (including the count confirmation adjustment path) and B5 alerts/activity/dashboard endpoints are not implemented. B3b serialized-item endpoints have since passed independent acceptance.
- Matching serial numbers against corrected numbers loads the items that have corrections; generating numbers loads all numbers once per request. Fine for the demonstration size; a production provider should index normalized historical numbers.

## Independent acceptance (2026-10-20)

- Build: zero warnings/errors. Full backend **511 passed**, zero failed/skipped (Domain 77, Application 12, Contracts 205, API 85, SQLite 132). Repeated after the B3b interruption; the preserved partial groundwork still builds and passes the same regression.
- First review found three test request-type compilation errors, missing aggregate manual/generated serial limit, and missing count-task subjects on frozen inventory refusals. Claude fixed all three classes of issue and added parser, temporary SQLite and HTTP tests. No failing test was removed or weakened.
- True inventory/API/browser **15 passed**: conversion, all-or-nothing state/inline creation, capacity reset/over-capacity, precision, replay/concurrent same key, immutable snapshots, selected-role permissions, concurrent oversell rejection, Unicode ledger paging, exact JSON types, stable physical item return IDs and actual browser receive/ship/refusal flows.
- Frontend **51 passed**, browser contract fixture **18 passed**. Operation mode now survives successful reload; details show translated actor roles and local times while preserving original data. Screenshot `artifacts/v2-real-ui/inventory.png` visually checked.
- V2 formatting verification passed; Codex mechanically normalized the new domain test file's line endings only. No package/service installed, old data changed, account reset, or public deployment performed. Test databases and credentials are temporary.
- Historical interruption before B3b completion: Claude reported `usage_limit_reached`, reset **2026-10-20 16:20 America/Vancouver**; this was not a token-count reading. After the user confirmed a reset, B3b resumed and passed acceptance (`docs/v2-b3-serials.md`). Claude still cannot provide verified remaining context/account quota numbers. Do not confuse internal token budget counters or maximum context capacity with remaining subscription tokens.

Re-run backend checks:

```powershell
dotnet build Wms.sln --no-restore
dotnet test tests/Wms.Domain.Tests --no-build
dotnet test tests/Wms.Persistence.Sqlite.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test tests/Wms.Api.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test Wms.sln --no-build
```
