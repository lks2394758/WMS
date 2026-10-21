# v2 B5a — alerts, activity log, dashboard

Status: B5a independently accepted by Codex on 2026-10-20 (America/Vancouver). B5b (failure audit at the framework level, final system acceptance) is not part of this step; the WMS is not declared complete.

Acceptance: build zero warnings/errors; all **541** backend tests passed (Domain 77, Application 12, Contracts 205, API 92, SQLite 155); V2 whitespace verified. Frontend client/proxy **51** and browser contract-fixture **18** checks passed; these are separate from the **8 real API/browser** checks in `prototypes/wms-test-ui/test/e2e/v2-real-monitoring-browser.mjs`. Real checks cover persisted dashboard figures, scopes, Unicode search before pagination, structured subject/failure filtering, UTC time boundaries, role/invalid-query rejection, immutable alert transitions, and actual dashboard/alert/history UI. All use temporary databases/profiles, not existing demo data. Owned test processes are closed.

Independent review found reversed time ranges returned an empty success page. This was corrected to 400 VALIDATION_FAILED and tested with timezone offsets; equal instants still give an empty range. A timing-based concurrent count start/cancel/dashboard read test was also added (limits below). UI now shows unresolved-investigation count, alert type/product/bin filters and retained query controls; alert details distinguish current master-data labels from immutable trigger evidence. Screenshots `artifacts/v2-real-ui/monitoring.png` (real failure evidence) and `artifacts/v2-ui/02-dashboard.png` (contract fixture layout) were visually inspected.

## Served (opt-in `V2:Enabled=true`, any signed-in role, read-only)

| Endpoint | Filters |
| --- | --- |
| `GET /api/v2/alerts` | `status=open\|resolved` (absent: both), `type`, `binId`, `productId`, `cursor`, `limit` |
| `GET /api/v2/activity-logs` | `search`, `from`, `to`, `actorId`, `productId`, `binId`, `type`, `success`, `cursor`, `limit` |
| `GET /api/v2/dashboard` | `scope=all\|mine` (default `all`) |

The DTOs are the existing ones in `MonitoringContracts.cs`. No schema change.

## Layers

- Ports: `src/Wms.Persistence.Abstractions/V2/MonitoringPortsV2.cs` (`IV2MonitoringReader`).
- SQLite: `src/Wms.Persistence.Sqlite/V2/V2SqliteMonitoring.cs`.
- Service: `src/Wms.Application/V2/Monitoring/V2MonitoringService.cs`.
- API: `src/Wms.Api/V2/V2MonitoringControllers.cs`.

## Rules

- Validation (`VALIDATION_FAILED`): unknown enum values (`status`, `type`, `scope`), invalid IDs, times and cursor/limit.
- Times: `from` is inclusive and `to` exclusive. Any ISO 8601 offset is accepted and compared in UTC.
- Order and paging: newest first by ID, keyset cursor, `limit` 1–200 (default 50).
- Exact filters run in SQL. `productId`/`binId` on the activity log match a subject of that type and ID in the stored subject JSON; `actorId` matches the actor.
- Activity `search` is Unicode case-insensitive (invariant upper case). It matches the description, actor name, login name, failure reason and subject labels, and is applied in batches before the page is cut.
- History is returned as written:
  - alert `trigger` (immutable by database trigger), `message`, `createdAt` and resolution;
  - log actor name and role, subjects with their labels at that time, and `changes`.
  - Only the alert's `binCode`/`productName` are current labels, because the alert row stores IDs only; archived bins and products are still shown.
- Dashboard:
  - `skuCount` counts non-archived products, zero stock included.
  - Also returned: open alerts; active counts (`inProgress` + `pendingConfirmation`); tasks with an unresolved investigation; the latest 10 log entries (all, or the caller's own for `mine`); the latest 10 open alerts.
  - `scope` changes only `recentActivity`. There is no cross-unit quantity.
  - All of it is read in one deferred SQLite read transaction (one snapshot, no writer lock).

## Limitations

- Activity filters on `productId`/`binId` use a substring match on the stored subject JSON (exact type + ID token); there is no index on it.
- The dashboard snapshot test (`Dashboard_reads_during_concurrent_commands_are_one_consistent_snapshot`) is timing-based. It reads while counts are started and cancelled, and checks that the latest log entry and the active-count figure agree. Whether the reads really overlap the commits is not guaranteed.
- `from` later than `to` is `VALIDATION_FAILED` on `to`. Equal instants (any offsets) give an empty page, because the range is `[from, to)`.

## Tests

- `tests/Wms.Persistence.Sqlite.Tests/V2/V2MonitoringServiceTests.cs`
- `tests/Wms.Api.Tests/V2/V2MonitoringApiTests.cs`

```
dotnet test tests/Wms.Persistence.Sqlite.Tests --filter FullyQualifiedName~V2MonitoringServiceTests
dotnet test tests/Wms.Api.Tests --filter FullyQualifiedName~V2MonitoringApiTests
```
