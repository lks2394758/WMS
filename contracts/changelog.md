# Contract changelog

## v1 (unreleased)

- Initial transport contracts: auth, catalog, inventory requests/responses, alerts, problem details, `inventory.changed.v1`.
- Added beyond the guide's minimum: `RefreshRequest`, `AlertResponse`, error codes `LOCATION_INACTIVE` and `NOT_FOUND`.
- Inventory write requests carry a `lines` array, so one transaction can cover several products.
- `contracts/openapi/wms.v1.json` is a hand-written skeleton until M2, when it is replaced by the generated document and compared in CI.
