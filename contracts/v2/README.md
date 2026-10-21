# API v2 contract

Status: **B0 contract** plus accepted runtime (process mode `V2:Enabled=true`) for **B1 `/auth/*`**, **B2a `/units`, `/categories`, `/products`**, **B2b `/bins` (including racks), `/users`, `/settings`**, **B3a `/inventory/receipts|shipments|movements|adjustments` and `/inventory/transactions`**, **B3b `/serialized-items` (list, detail, `/{id}/correct`)**, **B4 counts including confirm/resolve**, and **B5a `/dashboard`, `/alerts`, `/activity-logs`**. B4 was independently accepted 2026-10-20; see `docs/v2-b4-counts.md` (earlier phases: `docs/v2-b1-identity.md`, `docs/v2-b2a-catalog.md`, `docs/v2-b2b-admin.md`, `docs/v2-b3-inventory.md`, `docs/v2-b3-serials.md`). B5a was independently accepted 2026-10-20; see `docs/v2-b5-monitoring.md`. B5b framework rejection auditing and final prototype automated acceptance also passed (backend 545, real business/browser 62, real audit 7); see `docs/v2-prototype-acceptance.zh-CN.md`. Manual and production acceptance are not claimed. Runtime uses the new database `App_Data/wms-v2-demo.db`. v1 (`/api/v1`, `contracts/openapi/wms.v1.json`, `contracts/examples/*.json`) is unchanged.

Sources: `docs/database-redesign-decisions.zh-CN.md` (business rules, authoritative) and the frontend draft in `docs/v2-backend-handoff.zh-CN.md`. Deviations from the draft are listed at the end.

C# types: `src/Wms.Contracts/V2`. Examples: `contracts/examples/v2`. Tests: `tests/Wms.ContractTests/V2`.

## 1. Common rules

| Rule | Requirement |
| --- | --- |
| Base path | `/api/v2` |
| Format | UTF-8 JSON, camelCase, null properties omitted (`WmsJson`). Request members not in the contract are ignored by the server; clients must not rely on that |
| Time | ISO 8601 UTC with `Z`. Query `from`/`to` accept any ISO 8601 offset; `from` inclusive, `to` exclusive |
| IDs | Positive 64-bit integer as a JSON **string** of digits: no sign, leading zero, whitespace or exponent (`"1"` … `"9223372036854775807"`). JSON numbers are rejected. Same rule in paths and query strings. Validator `WireId` |
| Exact quantities | JSON **string**, never a number. Request: optional `-` (signed fields only), digits, optional `.` and 1-6 fraction digits; no `+`, exponent, grouping or whitespace; absolute value ≤ `999999999999.999999`. The product's `decimalPlaces` (0-6) is checked on the value (`"2.50"` is valid for 1 place). Response: canonical form without leading or trailing zeros (`"1.5"`, `"0"`, `"-1"`). Aggregates such as `totalQuantity` may exceed the single-value limit. Validator `WireQuantity`. Unit: always the product's base unit |
| Threshold percentages | JSON string `"0"`-`"100"`, at most 2 fraction digits (`WirePercent`). `occupancyPercent` is a display value = quantity / capacity × 100 rounded half-up to 2 places; it may exceed 100 |
| Business integers | `decimalPlaces`, `levels`, `cellsPerLevel`, `sessionHours`, `round`, `generateSerialCount`, counts and `version` are JSON numbers |
| Versions | Every mutable resource (unit, category, product, bin, user, settings, serialized item, count task) returns `version` (JSON number, int32). Updates and commands send back the version they were based on; a mismatch is `409 VERSION_CONFLICT` with the resource in `subjects`. Reload and retry |
| Lists | `?limit=&cursor=`; `limit` 1-200, default 50. Response `{ "items": [...], "nextCursor": "..." }`; `nextCursor` absent on the last page. Cursors are opaque. Lists never inline images |
| Search | `search` is trimmed, case-insensitive, substring match on the fields named per endpoint |
| Errors | `application/problem+json` (`ApiProblem`): `type,title,status,code,traceId`, optional `detail`, `errors` (field path → messages, e.g. `lines[0].quantity`), `subjects` (`[{type,id,label?}]`). Branch on `code` only |
| Idempotency | Client sends `Idempotency-Key: <UUID>` on every user action and reuses it (and the identical body) on retry. See §1.2 |
| Limits | Request body ≤ 4 MiB (`413 PAYLOAD_TOO_LARGE`), image ≤ 2 MiB decoded, ≤ 200 lines per inventory request, ≤ 10 000 serial numbers per request |
| Correlation | `X-Correlation-ID` as in v1 |
| Rate limiting | As in v1 (`429 RATE_LIMITED`, `Retry-After`) |

### 1.1 Role → permission matrix

Permissions follow the session's **current role** only, never the union of assigned roles.

| Permission | systemAdministrator | warehouseSupervisor | receivingClerk | shippingClerk |
| --- | --- | --- | --- | --- |
| `Catalog.Write` (units, categories, products, bins, racks) | ✓ | ✓ | | |
| `Inventory.Receive` | ✓ | ✓ | ✓ | |
| `Inventory.Ship` | ✓ | ✓ | | ✓ |
| `Inventory.Move` | ✓ | ✓ | | |
| `Inventory.Adjust` | ✓ | ✓ | | |
| `Inventory.Count` (start, submit, confirm, recount, cancel, resolve) | ✓ | ✓ | | |
| `SerialNumbers.Correct` | ✓ | ✓ | | |
| `Users.Manage` | ✓ | | | |
| `Settings.Manage` | ✓ | | | |

Exception: a receipt line may create a product and a unit inline (`newProduct`) with `Inventory.Receive` only; it grants no other catalog edit.

### 1.2 Retry semantics

| Write | Key / natural protection |
| --- | --- |
| Inventory receipts, shipments, movements, adjustments; count `confirm` | `Idempotency-Key` **required** (missing: `400`). Stored with the committed transaction (account + key unique, body digest). Same key + same body: the original response, header `Idempotent-Replayed: true`. Same key + different body: `409 IDEMPOTENCY_CONFLICT`. Rejections are not stored. The session must still be valid on retry |
| Count `submit` | Same round already submitted with the same `actualQuantity` and `note`: `200` with the current task. Different values: `409 COUNT_ROUND_ALREADY_SUBMITTED` |
| Count `recount`, `cancel`, `resolve`, serial `correct`, all `PUT` | `version` (and `round`): a retry after success gets `409 VERSION_CONFLICT`; reload shows the committed state. Correcting to the number the item already has is a no-op `200` |
| `POST /products`, `POST /counts` (start) | Implemented for products (B2a) and count start (B4). `Idempotency-Key` **required** (a UUID) (missing: `400`). The key, the account and a SHA-256 digest of the canonical request body are stored on the created row itself (`Products`/`CountTasks` technical columns `CreatedById`/`CreatorId`, `CreationRequestKey`, `CreationRequestDigest`; unique per account and key) in the same transaction as the creation. Same account + key + body: `201` with the **existing** product/task (current representation) and `Idempotent-Replayed: true`; nothing is created, frozen or numbered again. Same key + different body: `409 IDEMPOTENCY_CONFLICT`. Rejections store nothing. The body digest never includes credentials |
| Inline products in a receipt (`newProduct`) | Covered by the receipt's own key: a replayed receipt returns the original transaction, so the inline product is not created twice |
| Create unit / category / bin / rack / user | Natural unique key: a retry after success gets `409 ALREADY_EXISTS` with the existing object in `subjects`; the client reloads and treats it as done |
| `DELETE` (archive) | Already archived/deleted: `404 NOT_FOUND`; the client reloads and treats it as done |

Other writes accept the header and ignore it. No write may be retried blindly with a **new** key after an unknown outcome; retry with the same key and body, or reload first.

### 1.3 Problem codes

| Code | Status | When |
| --- | --- | --- |
| `VALIDATION_FAILED` | 400 | Field format, range, required member, conflicting members (`errors` names the fields) |
| `UNAUTHENTICATED` | 401 | No or unknown token |
| `INVALID_CREDENTIALS` | 401 | Login with wrong username/password or disabled account (indistinguishable) |
| `SESSION_EXPIRED` | 401 | Session past `expiresAt` |
| `SESSION_REVOKED` | 401 | Logged out, replaced by a newer login, or revoked by an account change |
| `FORBIDDEN` | 403 | Current role lacks the permission |
| `ROLE_NOT_ASSIGNED` | 403 | Login/switch to a role the account does not have |
| `NOT_FOUND` | 404 | Unknown or archived ID in the path, or referenced ID (`subjects`/`errors` say which) |
| `PAYLOAD_TOO_LARGE` | 413 | Body over the limit |
| `RATE_LIMITED` | 429 | |
| `INTERNAL_ERROR` | 500 | |
| `VERSION_CONFLICT` | 409 | Stale `version` |
| `IDEMPOTENCY_CONFLICT` | 409 | Key reused with another body |
| `CONCURRENCY_CONFLICT` | 409 | Write lock not obtained in time, a concurrent change won, or (login) the account changed between password check and session write; nothing stored; retry (same key for keyed writes) |
| `ALREADY_EXISTS` | 409 | SKU, barcode or serial number used now **or historically** by any object (archived included); username, unit name, sibling category name, bin position, rack |
| `IN_USE` | 409 | Delete of a unit/category still referenced (archived products included) or the preset unit |
| `STOCK_NOT_ZERO` | 409 | Change of SKU, barcode, unit, decimal places or serial tracking, product archive, bin edit/archive while stock exists |
| `OPEN_COUNT_OR_INVESTIGATION` | 409 | The same product changes, and moving or archiving a bin, while an unfinished count (the bin frozen) or an unresolved investigation exists; bin and count task in `subjects` |
| `ARCHIVED` | 422 | Operation on an archived product or bin |
| `INVALID_IMAGE` | 422 | Not real PNG/JPEG bytes, or type mismatch |
| `LAST_ADMINISTRATOR` | 409 | A user change would leave no enabled account with `systemAdministrator` |
| `THRESHOLD_CONFLICT` | 409 | New default thresholds would give a bin with one override an effective low ≥ high; bins listed in `subjects`, nothing saved |
| `BIN_FROZEN` | 409 | Inventory write touching a bin frozen by a count; task in `subjects` |
| `BIN_PRODUCT_MISMATCH` | 409 | Bin holds stock of another product |
| `INVESTIGATION_UNRESOLVED` | 409 | Putting another product into a bin whose count investigation is unresolved |
| `CAPACITY_REQUIRED` | 422 | Stocking an empty bin (quantity 0) without `capacity` |
| `CAPACITY_NOT_ALLOWED` | 422 | `capacity`/thresholds sent for a bin that already holds stock |
| `INSUFFICIENT_STOCK` | 409 | Result would be negative |
| `PRECISION_EXCEEDED` | 422 | Value (or packaging product) has more fraction digits than the product allows; never rounded |
| `QUANTITY_OUT_OF_RANGE` | 422 | Resulting bin quantity or packaging product beyond the limit |
| `SERIAL_NUMBERS_REQUIRED` | 422 | Serial-tracked line without serial numbers/generation, or serials on an untracked product |
| `SERIAL_COUNT_MISMATCH` | 422 | `quantity` differs from the number of items |
| `SERIAL_NOT_IN_BIN` | 409 | Item not in stock in the given source/adjusted bin, or belongs to another product |
| `SERIAL_ALREADY_IN_STOCK` | 409 | Receiving an item that is in stock |
| `SERIAL_NUMBER_SUPERSEDED` | 409 | A corrected (old) number used in an operation; current number in `subjects` |
| `SERIAL_TRACKING_DISABLED` | 422 | Returning a known serial item of a product whose tracking is now off |
| `COUNT_TASK_EXISTS` | 409 | Starting a count on a bin that has an active task or a task with an unresolved investigation (use that task's `recount`); task in `subjects` |
| `COUNT_BIN_HAS_NO_PRODUCT` | 422 | Starting a count on a bin with no current or retained product |
| `COUNT_STATE_CONFLICT` | 409 | Command not allowed in the task's status, or `round`/`resultId` is not the current one |
| `COUNT_ROUND_ALREADY_SUBMITTED` | 409 | Second submit of a round with different values |
| `TRANSACTION_NOT_ELIGIBLE` | 422 | `resolve` with a transaction that is not a committed adjustment of the task's bin and product made after the first unresolved result |

## 2. Endpoints

| Method and path | Permission | Request | Response |
| --- | --- | --- | --- |
| `POST /auth/login` | anonymous | `LoginRequest` | `200 LoginResponse` |
| `GET /auth/me` | signed in | — | `200 SessionResponse` |
| `POST /auth/switch-role` | signed in | `SwitchRoleRequest` | `200 SessionResponse` |
| `POST /auth/logout` | signed in | — | `204` |
| `GET /units` | signed in | `search` | `200 Page<UnitResponse>` |
| `GET /units/{id}` | signed in | — | `200 UnitResponse` |
| `POST /units` · `PUT /units/{id}` | `Catalog.Write` | `UnitWriteRequest` | `201`/`200 UnitResponse` |
| `DELETE /units/{id}` | `Catalog.Write` | `DeleteRequest`? | `204` |
| `GET /categories` | signed in | `search,parentId` | `200 Page<CategoryResponse>` (tree order) |
| `POST /categories` · `PUT /categories/{id}` | `Catalog.Write` | `CategoryWriteRequest` | `201`/`200 CategoryResponse` |
| `DELETE /categories/{id}` | `Catalog.Write` | `DeleteRequest`? | `204` |
| `GET /products` | signed in | `search,categoryId` | `200 Page<ProductListItem>` |
| `GET /products/{id}` | signed in | — | `200 ProductResponse` |
| `POST /products` | `Catalog.Write` | `ProductCreateRequest` | `201 ProductResponse` |
| `PUT /products/{id}` | `Catalog.Write` | `ProductUpdateRequest` | `200 ProductResponse` |
| `DELETE /products/{id}` (archive) | `Catalog.Write` | `DeleteRequest`? | `204` |
| `GET /bins` | signed in | `search,zone,rack,level,productId` | `200 Page<BinResponse>` (code order) |
| `GET /bins/{id}` | signed in | — | `200 BinResponse` |
| `POST /bins` · `PUT /bins/{id}` | `Catalog.Write` | `BinWriteRequest` | `201`/`200 BinResponse` |
| `DELETE /bins/{id}` (archive) | `Catalog.Write` | `DeleteRequest`? | `204` |
| `POST /bins/racks` | `Catalog.Write` | `RackCreateRequest` | `201 RackCreateResponse` |
| `GET /users` | `Users.Manage` | `search,role,enabled` | `200 Page<UserResponse>` |
| `GET /users/{id}` | `Users.Manage` | — | `200 UserResponse` |
| `POST /users` · `PUT /users/{id}` | `Users.Manage` | `UserCreateRequest` / `UserUpdateRequest` | `201`/`200 UserResponse` |
| `POST /users/{id}/reset-password` | `Users.Manage` | `ResetPasswordRequest` | `200 UserResponse` |
| `GET /settings` | signed in | — | `200 SettingsResponse` |
| `PUT /settings` | `Settings.Manage` | `SettingsUpdateRequest` | `200 SettingsResponse` |
| `POST /inventory/receipts` | `Inventory.Receive` | `InventoryOperationRequest` | `201 InventoryTransactionResponse` |
| `POST /inventory/shipments` | `Inventory.Ship` | `InventoryOperationRequest` | `201 InventoryTransactionResponse` |
| `POST /inventory/movements` | `Inventory.Move` | `InventoryOperationRequest` | `201 InventoryTransactionResponse` |
| `POST /inventory/adjustments` | `Inventory.Adjust` | `InventoryOperationRequest` | `201 InventoryTransactionResponse` |
| `GET /inventory/transactions` | signed in | `search,type,from,to,productId,binId,actorId` | `200 Page<TransactionListItem>` (newest first) |
| `GET /inventory/transactions/{id}` | signed in | — | `200 InventoryTransactionResponse` |
| `GET /serialized-items` | signed in | `search,productId,binId,inStock` | `200 Page<SerializedItemListItem>` |
| `GET /serialized-items/{id}` | signed in | — | `200 SerializedItemResponse` |
| `POST /serialized-items/{id}/correct` | `SerialNumbers.Correct` | `SerialCorrectionRequest` | `200 SerializedItemResponse` |
| `GET /counts` | signed in | `search,status,binId,unresolved` | `200 Page<CountTaskListItem>` (newest first) |
| `POST /counts` | `Inventory.Count` | `CountStartRequest` | `201 CountTaskResponse` |
| `GET /counts/{id}` | signed in | — | `200 CountTaskResponse` |
| `POST /counts/{id}/submit` | `Inventory.Count` | `CountSubmitRequest` | `200 CountTaskResponse` |
| `POST /counts/{id}/confirm` | `Inventory.Count` | `CountConfirmRequest` | `200 CountTaskResponse` |
| `POST /counts/{id}/recount` | `Inventory.Count` | `CountRecountRequest` | `200 CountTaskResponse` |
| `POST /counts/{id}/cancel` | `Inventory.Count` | `CountCancelRequest` | `200 CountTaskResponse` |
| `POST /counts/{id}/resolve` | `Inventory.Count` | `CountResolveRequest` | `200 CountResolutionResponse` |
| `GET /alerts` | signed in | `status=open\|resolved` (absent: both), `type,binId,productId` | `200 Page<AlertResponse>` (newest first) |
| `GET /activity-logs` | signed in | `search,from,to,actorId,productId,binId,type,success` | `200 Page<ActivityLogResponse>` (newest first) |
| `GET /dashboard` | signed in | `scope=all\|mine` (default `all`) | `200 DashboardResponse` |

Paths are relative to `/api/v2`. A request with an ID that is not a canonical ID string is `400`; an unknown one is `404`.

## 3. Authentication

- `POST /auth/login {username,password,role?}` → `{token,expiresAt,user}`. Username is trimmed and case-insensitive. A new login revokes the account's other session. `role` omitted: the first assigned role. Unassigned `role`: `403 ROLE_NOT_ASSIGNED`.
- `user` (`CurrentUser`): `id,username,displayName,roles,currentRole,permissions`; `permissions` derive from `currentRole` only.
- `token` is opaque; the server stores only its hash. Send `Authorization: Bearer <token>`. Every request checks the persisted session, so logout, a newer login, expiry, disable, role change or password reset end it at once (`401 SESSION_REVOKED`/`SESSION_EXPIRED`). No refresh token; `expiresAt` is fixed at login from `sessionHours` and not extended by role switches.
- `GET /auth/me` and `POST /auth/switch-role {role}` return `{user,expiresAt}`; the token is unchanged; switching to the current role is a no-op success. Each switch is logged.
- `POST /auth/logout` → `204`.
- No response contains a password, password hash or token hash; the token appears only in the login response. Failed logins are logged with a masked login name and no account.

## 4. Catalog

- **Units**: name 1-32 chars, trimmed, unique case-insensitively. The preset "件" (`isSystem`) cannot be deleted. A unit referenced by any product (archived included) cannot be deleted (`IN_USE`). Renaming corrects the name only; history keeps its snapshots.
- **Categories**: name 1-64, unique among siblings (case-insensitive; top level counts as siblings). `parentId` must not create a cycle (`VALIDATION_FAILED` on `parentId`). Delete only without children and product links (archived included). `GET /products?categoryId=` includes all descendants.
- **Products**:
  - `name` 1-128 (duplicates allowed). `sku`, `barcode` 1-128, no control characters or surrounding whitespace, unique case-insensitively against every current and historical SKU/barcode of every product. Empty on create: `SKU-000001`/`BC-000001` sequence, skipping used values.
  - `decimalPlaces` 0-6; `serialTracking` true requires 0.
  - Changing `sku`/`barcode` moves the old value to `historicalSkus`/`historicalBarcodes` (permanently reserved). That change, and any change of `unitId`, `decimalPlaces` or `serialTracking`, requires zero stock in all bins (`STOCK_NOT_ZERO`) and no unfinished count/unresolved investigation (`OPEN_COUNT_OR_INVESTIGATION`). Changing unit or precision clears the retained capacity and threshold overrides of the product's zero-stock bins and closes their capacity alerts.
  - Image: `imageBase64` (standard base64, ≤ 2 MiB decoded) with `imageContentType` `image/png` or `image/jpeg`, verified on the bytes (`INVALID_IMAGE`); one without the other is `400`. Update: no image members and `removeImage` absent/false keeps the image; a new image replaces it; `removeImage: true` removes it; `removeImage: true` plus an image is `400`. Only `GET /products/{id}` (and create/update responses) carry the image; lists carry `hasImage`.
  - `search` matches name, SKU and barcode (current values). `totalQuantity` is the sum over non-archived bins in the product's unit; no response adds quantities of different units.
  - `bins` lists every non-archived bin referencing the product, including zero-quantity bins that retained it.
  - `DELETE` archives: requires zero stock and no unfinished count/investigation; codes stay reserved; zero-stock bins drop the reference, capacity and overrides, and their capacity alerts close. Archived products disappear from lists, search and selection; history keeps snapshots.

## 5. Bins and racks

- `zone`, `rack`, `level`, `cell` 1-16 chars, no `-`, no whitespace or control characters. `code` = `zone-rack-level-cell` (≤ 128). Position unique (case-insensitive) among all bins **including archived ones** (`ALREADY_EXISTS`; no reuse of archived codes).
- `PUT` changes the position (new code; old code appended to `historicalCodes`, which never addresses a bin) and requires zero stock (`STOCK_NOT_ZERO`), no frozen or open count and no unresolved investigation (`OPEN_COUNT_OR_INVESTIGATION`; `BIN_FROZEN` and `INVESTIGATION_UNRESOLVED` are for inventory operations).
- `POST /bins/racks {zone,rack,levels?,cellsPerLevel?}`: omitted sizes use the current settings; 1-100 each, ≤ 10 000 bins. Level and cell labels are 2-digit 1-based (`01`…; 3 digits from 100). Fails as a whole if any position exists.
- `BinResponse`: product members describe the current or retained product; `quantity` is `"0"` when empty. `capacity`, `occupancyPercent` absent when no capacity is configured. `lowThreshold`/`highThreshold` are the bin's own overrides (absent = default); `effectiveLowThreshold`/`effectiveHighThreshold` are always present. `capacityStatus`: `notConfigured` (no product or capacity), `normal`, `low` (< low), `high` (> high), `overCapacity` (> 100%), strict comparisons, one state at a time. `frozenCountTaskId` is set while a count freezes the bin. `hasUnresolvedInvestigation` blocks putting another product into the bin.
- `search` matches code and current product name/SKU.
- `DELETE` archives: zero stock, no frozen task, no unresolved investigation.

## 6. Users and settings

- Username 1-64, no whitespace/control characters, unique case-insensitively; display name 1-100; `roles` non-empty, unique, fixed values; password 8-256 characters (never logged or returned).
- `PUT /users/{id}` with a change of roles or `enabled: false`, and every `reset-password`, revoke the account's session immediately. `LAST_ADMINISTRATOR` protects the last enabled administrator (including the caller). There is no user delete.
- Settings: `defaultRackLevels`, `defaultCellsPerLevel` 1-100; `lowThreshold < highThreshold`, both `WirePercent`; `sessionHours` 1-168. Rack defaults affect only new racks; `sessionHours` only new logins; default thresholds apply at once to bins without the matching override and re-evaluate their capacity alerts. A combination making any bin's effective low ≥ high is rejected as a whole (`THRESHOLD_CONFLICT`). The response is the saved settings with the new `version`.

## 7. Inventory operations

Envelope `InventoryOperationRequest {lines, source?, destination?, reason?, countResultId?}`; 1-200 lines; the whole request commits or fails. `source`/`destination` ≤ 200 chars; `reason` ≤ 2000 and required (non-blank) for adjustments; `countResultId` only on adjustments (links the transaction to the count result it corrects; the result must belong to a task on that bin and product).

### 7.1 Line members per operation

| Member | Receipt | Shipment | Movement | Adjustment |
| --- | --- | --- | --- | --- |
| `productId` | required unless `newProduct` | required | required | required |
| `newProduct` | optional (exclusive with `productId`) | — | — | — |
| `fromBinId` | — | required | required | — |
| `toBinId` | required | — | required (≠ from) | required (the adjusted bin) |
| `quantity` | > 0, or derived | > 0 | > 0 | non-zero signed delta |
| `capacity`, `lowThreshold`, `highThreshold` | empty target only | — | empty target only | empty target, positive delta only |
| `packagingUnit`, `packagingQuantity`, `conversionFactor` | optional | — | — | — |
| `serialNumbers` | tracked: manual | tracked: required | tracked: required | tracked: required |
| `generateSerialCount` | tracked: 1-10 000, exclusive with `serialNumbers` | — | — | tracked increase: allowed |

"—" means the member must be absent (`400`).

- **Quantity and precision**: every quantity must fit the product's `decimalPlaces` (`PRECISION_EXCEEDED`). Packaging: `packagingQuantity` and `conversionFactor` (> 0, ≤ 6 places) are multiplied exactly; the product must fit the product's precision and range, otherwise rejected, never rounded. When packaging is sent, `quantity` may be omitted; if sent it must equal the product. `packagingUnit` 1-32 chars, free text, stored on the line only.
- **One product per bin**: the target must be empty (quantity 0) or hold the same product (`BIN_PRODUCT_MISMATCH`); a bin with an unresolved investigation accepts only its own product (`INVESTIGATION_UNRESOLVED`). A zero-quantity target needs `capacity` (`CAPACITY_REQUIRED`) and starts with default thresholds unless overrides are sent; a target with stock rejects them (`CAPACITY_NOT_ALLOWED`). Over-capacity is allowed and alerts.
- **Frozen bins**: any line touching a frozen bin fails (`BIN_FROZEN`).
- **Archived** product or bin: `ARCHIVED`.
- **Serial-tracked products** (`decimalPlaces` 0): `quantity` is the number of items; when sent it must equal it (`SERIAL_COUNT_MISMATCH`). Numbers are trimmed, unique within the request (case-insensitive) and must be current numbers: an old corrected number is `SERIAL_NUMBER_SUPERSEDED`.
  - Receipt: a new number creates an item; a known out-of-stock number of the same product returns that item (same ID, continuous history); an in-stock one is `SERIAL_ALREADY_IN_STOCK`; one of another product is `ALREADY_EXISTS`. `generateSerialCount` creates `SN-000001`-style numbers, skipping used values.
  - Shipment / movement / negative adjustment: each item must be in stock in the source (adjusted) bin and belong to the product (`SERIAL_NOT_IN_BIN`). The negative adjustment `quantity` is `-count`.
  - Positive adjustment: as receipt (manual or generated).
  - A known item of a product whose tracking is now off: `SERIAL_TRACKING_DISABLED`.
- Untracked products must not send serial members (`SERIAL_NUMBERS_REQUIRED` explains both directions).
- **Inline product** (`newProduct`, receipts only): same rules as product create, plus exactly one of `unitId` and `newUnitName` (an existing name case-insensitively reuses that unit). The product and the unit are created in the same transaction and logged; no categories or image.
- **Response**: `InventoryTransactionResponse`, number `TX-000001`. Each line has product and bin snapshots, before/after quantities of each side (adjustments use the `to*` side), packaging, and `items` (serial ID + number at that time). Transactions are immutable; correct them with a new operation.

## 8. Serialized items

- Search matches current and historical numbers (case-insensitive); a row found by an old number carries `matchedHistoricalSerialNumber`. Filters `productId`, `binId`, `inStock`. The frontend picker uses `GET /serialized-items?productId=&binId=&inStock=true`.
- `history` lists every transaction line touching the item with the number as it was then; `corrections` lists corrections from the activity log.
- `POST /serialized-items/{id}/correct {serialNumber,reason,version}`: new number obeys the global uniqueness rule against all current and historical numbers; the old number moves to `historicalSerialNumbers` forever; the ID, transactions and snapshots are unchanged; `reason` required. Logged with old and new number. The `version` is checked first as in §1 (stale → `409 VERSION_CONFLICT`, also for the exact current number); with the current `version`, the same number as the current one is a no-op `200` (no log, no version change); a case-only change is refused as the same identity (`ALREADY_EXISTS`).

## 9. Counts

One task counts one bin. Blind: while a round is open, no response contains its book quantity. Submitted results are immutable and visible (book, actual, difference) at all times.

### 9.1 State machine

| From | Command | To | Freeze |
| --- | --- | --- | --- |
| — | `POST /counts {binId}` | `inProgress`, round 1, snapshot persisted | frozen |
| `inProgress` | `submit {actualQuantity,round,version,note?,resultId?}` | `pendingConfirmation`; new immutable result (snapshot + actual) | frozen |
| `inProgress` round 1 | `cancel {reason,round,version}` | `cancelled` | released |
| `inProgress` from pending | `cancel` | `pendingConfirmation`, previous latest result again current | **stays frozen** |
| `inProgress` from completed | `cancel` | `completed`, previous `completedAt` restored | released |
| `pendingConfirmation` | `confirm {resultId,round,version,reason?}` | `completed` | released |
| `pendingConfirmation` | `recount {reason,round,version,resultId?}` | `inProgress`, round + 1, new snapshot from live stock | frozen |
| `completed` with unresolved investigation | `recount` | `inProgress`, round + 1, `completedAt` cleared, new snapshot | re-frozen |
| `completed` with unresolved investigation | `resolve {conclusion,noAdjustmentNeeded,version,transactionId?,resultId?,round?}` | `completed`, investigation closed | — |

- Start requires the bin to have a current or retained product (`COUNT_BIN_HAS_NO_PRODUCT`), no other active task, and no task with an unresolved investigation on the bin (`COUNT_TASK_EXISTS`, use that task's `recount`). Number `CT-000001`.
- Every command checks `version`, `round` = the task's current round and, when sent, `resultId` = the latest result (`VERSION_CONFLICT` / `COUNT_STATE_CONFLICT`). `confirm` requires `resultId` and acts only on the latest result.
- `confirm`:
  - difference 0: completes, no transaction;
  - untracked product with a difference: `reason` required; in the same transaction the bin is set to the result's actual quantity (the bin was frozen, so the book is still valid), an `adjustment` transaction linked to the result is written, the task completes and the bin is released. `Idempotency-Key` required;
  - serial-tracked product with a difference: no stock or item change; the task completes, the bin is released, one `countDiscrepancy` alert opens per task (linked to the first unresolved result) and the result becomes `underInvestigation`.
- Cancelled rounds keep their number; cancel never creates a result. Cancel and its round snapshot are logged.
- `resolve`: exactly one of `transactionId` and `noAdjustmentNeeded: true`, and a non-blank `conclusion`. The transaction must be an eligible adjustment (`TRANSACTION_NOT_ELIGIBLE`); its quantity need not equal the old difference. It covers **all** unresolved results of the task (returned in `resolvedResultIds`), writes one immutable log entry, closes the investigation alert. Not allowed while a recount round is open. A zero-difference recount never resolves automatically.
- `CountTaskResponse.allowedActions` lists commands available to the current session; `statusBeforeRound` (only while `inProgress`) is what `cancel` restores.
- Result `disposition` (derived): `pending`, `superseded`, `confirmedNoDifference`, `adjusted` (+ `adjustmentTransactionId`), `underInvestigation`, `resolved` (+ `resolutionLogId`).

## 10. Alerts, activity log, dashboard

- **Alerts** (read-only, no acknowledgement): `lowStock`, `highStock`, `overCapacity` (one capacity alert per bin at a time; a state change closes the old and opens a new one; a continuing state never duplicates) and `countDiscrepancy` (one per task). Status `open`/`resolved`. `trigger` is the snapshot at opening (quantity, capacity, occupancy, effective thresholds, unit, precision, SKU; for discrepancies round, book, actual, difference); never rewritten. Capacity alerts close automatically when the state ends; discrepancy alerts close only by `resolve`.
- **Activity log** (append-only, success and failure): `type` from `ActivityTypes`; `subjects` lists every related product, bin, transaction, task, result, item, user, unit, category or settings; `changes` lists changed fields with before/after text (images summarized as type and size; passwords never recorded). Failed operations are logged even though the business transaction rolled back. `productId`/`binId`/`actorId` filters match subjects/actor.
- **Dashboard**: `skuCount` = non-archived products including zero stock; `openAlertCount`; `activeCountCount` = tasks `inProgress` or `pendingConfirmation`; `unresolvedInvestigationCount`; `recentActivity` = latest 10 log entries of the warehouse (`scope=all`) or of the caller (`mine`); `alerts` = latest 10 open alerts. No cross-unit total.

## 11. Differences from the frontend draft (`docs/v2-backend-handoff.zh-CN.md`)

| Draft | Contract | Reason |
| --- | --- | --- |
| `me`/`switch-role` return user or `{user}`, maybe a new token | Always `{user,expiresAt}`, token unchanged | One shape; the session row holds the role (compatible with `data.user ?? data`) |
| Login `{username,password}` | Optional `role` added | Choose identity at login |
| `logout` | `204`, no body | |
| One product body with `version?` | `ProductCreateRequest` / `ProductUpdateRequest`; `version` required on update; `removeImage` added; null `sku`/`barcode` on update keep the value | Stale edits; explicit image removal |
| Product list fields | `+hasImage`; no image bytes in lists | Payload size |
| Product detail `bins` | `ProductBinStock` rows (`binId,binCode,quantity,capacityStatus,capacity?,occupancyPercent?,frozenCountTaskId?`) | |
| Bin list fields | `+zone,rack,level,cell,sku,decimalPlaces,serialTracking,capacityStatus,lowThreshold?,highThreshold?,effectiveLowThreshold,effectiveHighThreshold,hasUnresolvedInvestigation,historicalCodes`; `productId` etc. absent on a never-used bin | Filters and alerts state |
| Bins `racks` `{zone,rack,levels,cellsPerLevel}` | `levels`/`cellsPerLevel` optional (default from settings) | |
| `DELETE` body | Optional `{version?}` | Frontend sends `{}`; compatible |
| User password at create only | Password 8-256; no user delete; `LAST_ADMINISTRATOR` | Lockout protection |
| Settings | Response adds `version,updatedAt,updatedById,updatedByName`; thresholds are strings | Exact percentages |
| Inventory line set | Adopted unchanged. Envelope adds `countResultId` (adjustments) | Link investigation adjustments to the result |
| Adjustment bin | `toBinId` as in the draft; response uses the `to*` side | Kept for compatibility |
| Transactions list `id,number,type,actorName,actorRole,occurredAt` | `+actorId,lineCount,source,destination,reason,countResultId`; no `count` type in v2 | |
| Serialized items list | `+productId,sku,binId,version,matchedHistoricalSerialNumber`; detail splits `history` and `corrections` | Picker filters; old-number hint |
| Serial correct `{serialNumber,reason}` | `version` required | Stale detection |
| Counts list includes `results` | List rows omit `results`; `+binId,productName,creatorRole`; detail adds `allowedActions,currentRound,statusBeforeRound,cancelledAt,cancelReason` | Size; blind display |
| Count `submit/confirm/recount/cancel` with optional `round/version/resultId` | `round` and `version` required on all four; `resultId` required on `confirm`, optional elsewhere | Stale-command detection |
| `resolve` returns the task | `CountResolutionResponse {task,resolvedResultIds,activityLogId,transactionId?}`; `version` required | Server lists covered results |
| Alerts `id,type,productName,binCode,createdAt,status` | `+binId,productId,message,trigger,countTaskId,countResultId,resolvedAt,resolutionNote`; filter values `open`/`resolved`, absent = all | Trigger snapshot |
| Activity log row | `+id,actorId,subjects,loginName,failureCode,changes` | Structured subjects |
| Dashboard | `+unresolvedInvestigationCount,scope`; `recentActivity`/`alerts` capped at 10 | |
| `version` type unspecified | JSON number (int32) everywhere | One wire type |

## 12. Migration and isolation

- v1 stays as is: UUID IDs, `int` quantities, JWT access + refresh token, single `role`. No v1 client changes.
- v2 uses only the new file `App_Data/wms-v2-demo.db` (from B1). It never opens, migrates or seeds `wms-demo.db`; no v1 data, account or password is copied or reset. New demo data: four role accounts (password from secure configuration), the unit "件" and default settings; no stock or transactions.
- v2 tokens are not valid on v1 and v1 tokens are not valid on v2.
- Quantities are stored as micro-unit 64-bit integers; the same convention applies to a later SQL Server/PostgreSQL provider. SQLite results prove nothing about production concurrency (AGENTS.md rule 4).
