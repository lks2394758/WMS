# API conventions (v1)

These rules apply to every endpoint. They are the contract an independent client team builds against.

| Rule | Requirement |
| --- | --- |
| Base path | `/api/v1` |
| Format | UTF-8 JSON, `camelCase` property names, null properties omitted |
| Authentication | `Authorization: Bearer <access-token>` (15-minute access token, 8-hour rotating refresh token) |
| Time | ISO 8601 UTC with `Z`, e.g. `2026-10-10T18:00:00Z` |
| Identifiers | UUID strings |
| Lists | Cursor pagination: `?cursor=&limit=`, response `{ "items": [...], "nextCursor": "..." }`; `nextCursor` absent on the last page |
| Inventory writes | `POST` with header `Idempotency-Key: <UUID>` |
| Errors | `application/problem+json` with stable `code` and `traceId` |
| Versioning | Breaking changes require `/api/v2`; v1 is never silently broken |

## Inventory writes and idempotency

- Generate one UUID per user action, and reuse it for every retry of that action.
- Same user, same key, same body: the original response is returned and stock does not change again.
- Same user, same key, different body: `409` with code `IDEMPOTENCY_CONFLICT`.
- Business rejections (for example `INSUFFICIENT_STOCK`) are not stored; a retry is evaluated again.
- Keys are kept 24 hours.
- Never send stock quantities you expect the server to store. The server locks, recalculates and returns the final balances.

## Error contract

```json
{
  "type": "https://wms-api.company.intra/problems/insufficient-stock",
  "title": "Insufficient available stock",
  "status": 409,
  "code": "INSUFFICIENT_STOCK",
  "detail": "Location A-01-01 has 2 available units; 3 cannot be removed.",
  "traceId": "00-..."
}
```

Validation failures add `errors`: a map of field name to messages. Clients branch on `code`, never on `title` or `detail`.

| Code | HTTP status |
| --- | --- |
| `VALIDATION_FAILED` | 400 |
| `UNAUTHENTICATED` | 401 |
| `FORBIDDEN` | 403 |
| `NOT_FOUND`, `PRODUCT_NOT_FOUND`, `LOCATION_NOT_FOUND` | 404 |
| `PRODUCT_INACTIVE`, `LOCATION_INACTIVE` | 422 |
| `BARCODE_AMBIGUOUS`, `INSUFFICIENT_STOCK`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_CONFLICT` | 409 |
| `INTERNAL_ERROR` | 500 |

## Wire values

- Roles: `systemAdministrator`, `warehouseSupervisor`, `receivingClerk`, `shippingClerk`
- Transaction types: `receipt`, `movement`, `shipment`, `count`, `adjustment`
- Alert types: `lowStock`, `overstock`; statuses: `opened`, `acknowledged`, `closed`
- Transaction numbers: `RCV|MOV|SHP|CNT|ADJ-YYYYMMDD-######`

## Real-time events

SignalR hub `/hubs/inventory` only tells clients to refresh; it never accepts inventory changes. See `contracts/events/inventory-events.v1.md`.

## Examples

Every request and response type has a JSON example in `contracts/examples`. `Wms.ContractTests` deserializes each one strictly and checks that it round-trips unchanged.
