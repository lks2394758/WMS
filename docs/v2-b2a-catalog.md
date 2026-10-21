# API v2 — B2a: units, categories, products

Status: **B2a independently accepted by Codex on 2026-10-19**. Build: zero warnings/errors; full backend regression: 445 passed (Domain 60, Application 12, Contracts 205, API 76, SQLite 92). Real browser + Kestrel + temporary SQLite catalog checks: 6 passed, including Unicode search and pagination; real auth/proxy regression: 7 passed. V2 whitespace checks passed. These are prototype SQLite results, not production concurrency proof (AGENTS.md rule 4). Endpoints, DTOs and codes: `contracts/v2/README.md` §1-4. No schema change: the frozen initial migration already holds every column used.

## Layers

| Layer | Files |
| --- | --- |
| Ports | `Wms.Persistence.Abstractions/V2/CatalogPortsV2.cs` (`IV2CatalogReader`, `IV2CatalogStore`, `IV2CatalogWriteScope`) |
| Rules | `Wms.Application/V2/Catalog/*` (`V2UnitService`, `V2CategoryService`, `V2ProductService`, `V2ImageValidator`, `V2CatalogMapping`), `V2/Common/*` (`V2CatalogWriter`, paging, text, exact quantities) |
| SQLite | `Wms.Persistence.Sqlite/V2/V2SqliteCatalog.cs` (reads, write scope), `V2CatalogRows.cs`, mappings in `WmsV2DbContext` |
| HTTP | `Wms.Api/V2/V2CatalogControllers.cs` (thin), registered only in `V2ControllerFeatureProvider` |

## Rules implemented

- **Every write** runs in one `BEGIN IMMEDIATE` transaction on the shared v2 writer queue. The clock is read only after the transaction is open, so time spent waiting in the queue counts. Inside the transaction the caller's session is re-read: open, not expired at that time, account enabled, current role still assigned and granting `Catalog.Write` (current role only, never the union). A request whose session expired, was revoked or switched role while it waited cannot write. The success log entry (with field changes) commits in the same transaction; a business failure rolls everything back and is logged separately (`Success = 0`, code and reason).
- **Failure log identity** is the one verified inside the transaction: account + current session role when the session is still valid (e.g. `FORBIDDEN` after a switch to the receiving role is logged with `receivingClerk`); account without a role when the session is no longer valid (expired, revoked, disabled, role removed); no actor when the session row is missing. The role from the request's earlier authentication is never reused.
- **Rejected requests are audited**: a write whose request fails the use case's own validation (ID, name, version, `Idempotency-Key`, image, `removeImage` conflict, ...) is re-checked in a write transaction and logged as a failure with the verified identity and stable `failureCode`; nothing changes. Logs hold descriptions, codes and reasons only: never the request body, image content (only type and size in change records) or credentials.
- **Not audited (known, cross-cutting)**: rejections that never reach the use case: framework model binding/JSON errors (`400` from malformed or incomplete bodies), the authorization policy `403` for a role without `Catalog.Write`, `401`, rate limiting and body size. They are visible in the request log only.
- **Units**: trimmed 1-32, unique by invariant upper case (Unicode-aware). Renames bump `version`. The preset "件" and any unit used by a product (archived included) cannot be deleted (`IN_USE`).
- **Categories**: trimmed 1-64, unique among siblings (top level is one sibling group); no self parent or cycle (`VALIDATION_FAILED` on `parentId`); children or product links (archived included) block deletion. Lists are in path order; `GET /products?categoryId=` includes all descendants.
- **Products**:
  - SKU and barcode are unique by invariant upper case against every current and historical code of every product, archived included (checked in the application inside the transaction; the database keeps unique normalized current codes). Changing a code appends the old one to the history; a code can never be changed to any historical code.
  - Empty SKU/barcode → `SKU-000001` / `BC-000001` from the SystemSettings counters, incremented inside the transaction and skipping any used code; numbers survive restarts; a rolled-back create consumes nothing.
  - `POST` needs `Idempotency-Key` (UUID). Actor, key and a SHA-256 digest of the canonical body (trimmed name, sorted category IDs, image as the hash of its base64) are stored on the row. Same actor + key + body → `201` with the existing product, `Idempotent-Replayed: true`, nothing created or numbered; another body → `409 IDEMPOTENCY_CONFLICT`; another account may use the same key.
  - SKU, barcode, unit, decimal places, serial tracking and archiving require zero stock in every bin (`STOCK_NOT_ZERO`) and no unfinished count or unresolved investigation on the product's bins, open round or results (`OPEN_COUNT_OR_INVESTIGATION`). Name, categories and image can change at any time.
  - A unit or precision change clears capacity and threshold overrides of the product's zero-stock bins (reference kept) and resolves its open capacity alerts. Archiving also drops the reference from those bins (they become fully free). Alerts are resolved, never deleted; ledger snapshots are untouched.
  - Archived products: `GET /products/{id}` → 404, not listed or searched, `PUT` → `422 ARCHIVED`, `DELETE` again → 404; their codes stay reserved. Retrying the original create (same key and body) still returns that product as a replay and creates or numbers nothing.
  - Search: name, SKU and barcode match as substrings case-insensitively by Unicode invariant upper case (`écrou` ↔ `ÉCROU`). SQLite folds ASCII only, so the provider streams candidates in ID order, matches them in application code and cuts the page afterwards; SKU and barcode use their normalized columns.
  - The create digest is computed from the validated fields (a missing category list equals an empty one).
  - Detail: image, historical codes, per-bin quantity/capacity/status/occupancy and an exact cross-bin total (128-bit sum, never floating point; may exceed the single-value limit).
- **Images**: base64 ≤ 2 MiB decoded; `imageContentType` must match the real bytes. PNG: chunk lengths, letter types, CRC-32, exactly one legal IHDR first, PLTE rules, consecutive non-empty IDAT data, no unknown critical chunk, empty IEND last, and the zlib data must inflate to exactly the size the header implies with valid scanline filter bytes (non-interlaced). JPEG: SOI, well-formed segments, a sized SOFn before SOS, scan data, EOI. This is a structural check, not a full pixel decode. `removeImage: true` with a new image → 400.

## For the frontend

- All IDs are strings; `version` is a number and must be sent back on `PUT` (units, categories: `version` in the body; products: required).
- `POST /products` must send `Idempotency-Key`; keep the same key and body when retrying an unknown outcome.
- Product list rows have `hasImage` and no image; load `GET /products/{id}` for `imageBase64`/`imageContentType`.
- `sku`/`barcode` `null` on `PUT` keep the current value; on `POST` they generate codes.
- `DELETE` accepts `{}` or `{ "version": n }`.
- Problems: branch on `code`; `errors` names the field (also for `ALREADY_EXISTS` on `sku`/`barcode`/`name`); `subjects` names the conflicting objects.
- Product edits need only `GET /units`, `GET /categories`, `GET /products/{id}`; bins are not required.

## Not in B2a

B2b bins/racks writes, users, settings; B3 inventory; B4 counts; B5 alerts, activity-log and dashboard endpoints. Their routes answer 404.

## Tests

```powershell
dotnet build Wms.sln --no-restore
dotnet test tests/Wms.Persistence.Sqlite.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test tests/Wms.Api.Tests --no-build --filter "FullyQualifiedName~V2"
dotnet test Wms.sln --no-build
```
