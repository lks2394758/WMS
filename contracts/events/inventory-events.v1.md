# Inventory events v1

## Connection

- Hub: `/hubs/inventory`, WebSocket transport, same HTTPS host as the API.
- Authentication: the access token. Browsers and SignalR clients that cannot set headers pass it as the `access_token` query value.
- On connect, the server adds the connection to its warehouse group. The hub exposes **no** client-callable methods.

## Event `inventory.changed.v1`

Client method name: `InventoryChanged`. The server sends it only after the inventory transaction has committed.

```json
{
  "eventId": "0d0d5d58-8661-433a-89ae-b7dd25d9e56e",
  "eventType": "inventory.changed.v1",
  "occurredAt": "2026-10-10T18:00:00Z",
  "warehouseId": "3f6c1f2e-9a4b-4c1d-8e2f-1a2b3c4d5e01",
  "transactionNo": "SHP-20261010-000042",
  "productId": "5d2c3b4a-1e2f-4a3b-8c4d-5e6f7a8b9c04",
  "affectedBalances": [
    { "locationId": "7b1e2d3c-4a5b-4c6d-9e8f-0a1b2c3d4e02", "availableQuantity": 15 }
  ],
  "alertChanges": [
    { "type": "lowStock", "status": "opened" }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `eventId` | Unique per event; use it to drop duplicates |
| `transactionNo` | The committed transaction that caused the change |
| `productId` | One event per product; a multi-product transaction emits several events |
| `affectedBalances` | Final available quantity per touched location |
| `alertChanges` | Alerts opened or closed by this transaction; empty when none changed |

## Client rules

1. Delivery is **at least once**: de-duplicate by `eventId`.
2. Events are hints, not the system of record. After every reconnect, reload the affected data through the REST API.
3. A successful write is proven only by its REST response, never by an event.
4. Target latency is under 2 seconds from commit; events committed while the dispatcher is down are delivered after it restarts.
