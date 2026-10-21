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
- Same user, same key, same body: the original response is returned with header `Idempotent-Replayed: true`, and stock does not change again.
- Same user, same key, different body: `409` with code `IDEMPOTENCY_CONFLICT`.
- Business rejections (for example `INSUFFICIENT_STOCK`) are not stored; a retry is evaluated again.
- `409 CONCURRENCY_CONFLICT`: the write could not get the inventory lock in time. Nothing was stored; retry the same action with the same key.
- Keys are kept 24 hours.
- Never send stock quantities you expect the server to store. The server locks, recalculates and returns the final balances.

## Endpoints and permissions (v1)

Every endpoint requires a signed-in user unless marked anonymous. Writes also require the listed permission policy.

| Endpoint | Permission |
| --- | --- |
| `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh` | anonymous |
| `GET /api/v1/auth/me` | signed in |
| `GET /api/v1/products`, `/products/{id}`, `/locations`, `/locations/{id}`, `/barcodes/resolve?value=` | signed in |
| `GET /api/v1/inventory/balances?productId=&locationId=` | signed in |
| `POST /api/v1/inventory/receipts`, `/inventory/movements` | `Inventory.Receive` |
| `POST /api/v1/inventory/shipments` | `Inventory.Ship` |
| `POST /api/v1/inventory/counts` | `Inventory.Count` |
| `POST /api/v1/inventory/adjustments` | `Inventory.Adjust` |
| `GET /health/live`, `GET /health/ready`, `GET /openapi/v1.json` | anonymous, not rate limited |

## Correlation, rate limiting and health

- `X-Correlation-ID`: optional request header, 1-64 characters from `[A-Za-z0-9._:-]`. Every response (errors and health included) echoes it; a missing or invalid value is replaced by a generated 32-character hex ID. It is an opaque token: never put user data in it.
- Rate limiting: each signed-in user (or, before sign-in, each client address) has a fixed window of 120 requests per minute across the `/api` endpoints. Excess requests get `429` with code `RATE_LIMITED` and a `Retry-After` header (seconds). Health checks and the OpenAPI document are never limited.
- `GET /health/live`: the process is up; runs no checks. `GET /health/ready`: runs the named readiness checks `persistence` (a catalog read through the registered provider) and `jwt-signing` (the signing key signs and verifies). `200 Healthy` or `503 Unhealthy` as `text/plain`; failure details are never returned.
- The server logs one entry per completed request with method, endpoint route template, status and duration, inside the correlation scope. Query strings, request bodies, credentials and token values are never logged.

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
| `RATE_LIMITED` | 429 |
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
