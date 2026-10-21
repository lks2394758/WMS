# v2 B4 — count tasks, confirmation and investigation resolution

Status: B4a and B4b independently accepted by Codex on 2026-10-20 (America/Vancouver). Count lifecycle, confirmation and investigation resolution are implemented. Monitoring query endpoints and framework-level failure auditing remain B5.

Latest acceptance: build zero warnings/errors; all **536** backend tests passed (Domain 77, Application 12, Contracts 205, API 91, SQLite 151); V2 whitespace check passed. All **7** real API/browser count checks passed using `node test/e2e/v2-real-count-browser.mjs`, temporary SQLite and an isolated existing browser. These include restored pending-result confirmation after a cancelled round, exact adjustment, replay, serial discrepancy without stock changes, zero-difference recount preserving investigation, eligibility/roles, and UI confirmation/resolution evidence. No legacy database/accounts changed; owned test processes were cleaned up.

Independent review correction: external count detail now uses one deferred SQLite read transaction for task/results/evidence, avoiding mixed-version responses without taking a writer lock. A 15-round concurrent command/read stress test passed; it is timing-based, not deterministic interleaving proof and not production-provider concurrency acceptance.

Historical B4a acceptance: 529 backend tests passed, zero build warnings/errors and V2 whitespace verification passed. Success-log product/result references were corrected and tested. Browser count acceptance was added in B4b above.

## Served (opt-in `V2:Enabled=true`)

| Endpoint | Permission |
| --- | --- |
| `GET /api/v2/counts?search=&status=&binId=&unresolved=&cursor=&limit=` | signed in |
| `GET /api/v2/counts/{id}` | signed in |
| `POST /api/v2/counts` (`Idempotency-Key` UUID required) | `Inventory.Count` |
| `POST /api/v2/counts/{id}/submit` · `/recount` · `/cancel` | `Inventory.Count` |
| `POST /api/v2/counts/{id}/confirm` (`Idempotency-Key` UUID required) · `/resolve` | `Inventory.Count` |

B4b (confirm, resolve) passed independent backend and real browser/API acceptance.

## Layers

- Ports: `src/Wms.Persistence.Abstractions/V2/CountPortsV2.cs` (`IV2CountReader`, `IV2CountWriteOperations` on the shared write scope).
- SQLite: `src/Wms.Persistence.Sqlite/V2/V2SqliteCounts.cs`; `CountTasks.Version` is a concurrency token. Frozen initial migration unchanged.
- Service: `src/Wms.Application/V2/Counts/V2CountService.cs`. API: `src/Wms.Api/V2/V2CountsController.cs`.

## Rules

- Every command runs in the single writer transaction (`BEGIN IMMEDIATE`) with the session, account and **current role** re-checked; success and failure are written to the immutable activity log (failures in their own transaction).
- Guards in order: `version` (`VERSION_CONFLICT`), `round` = current round, `resultId` (when sent) = latest result (`COUNT_STATE_CONFLICT`), then the status (`COUNT_STATE_CONFLICT`).
- Submit retry: when the current round already has a result, the same actual quantity and note answer `200` with the current task; other values answer `COUNT_ROUND_ALREADY_SUBMITTED`. This check runs before the version check, because the first submit already changed the version.
- Start: the bin must be known and not archived (`NOT_FOUND`/`ARCHIVED`). It must have a current or retained product (`COUNT_BIN_HAS_NO_PRODUCT`) and no active task or unresolved investigation (`COUNT_TASK_EXISTS`, task in subjects). The number is `CT-000001`. The round snapshot (product, unit, precision, book) is stored on the task and the bin is frozen.
- Idempotency: key + SHA-256 of the canonical body are stored on the task. A replay answers `201` with the current task and `Idempotent-Replayed: true`. Another body with the same key answers `IDEMPOTENCY_CONFLICT`. Rejections store nothing.
- Blind: the open round's book quantity is never returned (`currentRound` has no book). Results are append-only (database trigger) and show book, actual and difference. Quantities are exact decimal strings within the snapshot precision (`PRECISION_EXCEEDED`).
- Rounds only grow. Cancel creates no result and the cancelled round number stays used. Cancel restores the earlier state:
  - from round 1: `cancelled` (with `cancelledAt` and reason), bin released;
  - from pending: `pendingConfirmation`, bin stays frozen;
  - from completed: `completed` with the earlier `completedAt`, bin released.
  - The reason of a non-terminal cancel and of a recount is kept only in the log.
- Recount: allowed from `pendingConfirmation`, or from `completed` with an unresolved investigation. Each recount takes a new live snapshot.

## B4b: confirm and resolve

- Confirm (`pendingConfirmation` only; `resultId` = latest result; current `round`, so a cancelled round's number counts) runs entirely inside the count writer transaction. There is no nested writer, and the generic inventory endpoints still refuse frozen bins:
  - Difference 0: the task completes and the bin is released. No transaction.
  - Untracked product with a difference (`reason` required): the bin is set to the actual quantity (checked against the frozen book, otherwise `CONCURRENCY_CONFLICT` and full rollback). An immutable `adjustment` transaction linked to the result (`CountResultId`) is written, capacity alerts are reconciled, the task completes and the bin is released.
  - Serial-tracked product with a difference: no stock or item change. `hasUnresolvedInvestigation` is set and the task completes with the bin released. One `countDiscrepancy` alert opens per task (unique index), linked to the first unresolved result. Later rounds never open a second alert.
- Confirm idempotency:
  - The actor's key and the SHA-256 of the canonical body are stored in the immutable `countConfirm` success log entry, and also on the adjustment row when there is one.
  - Replay order: the session and current role are re-validated first, then the key is looked up. The same body gives `200`, the current task and `Idempotent-Replayed: true`, with no second effect. A different body gives `IDEMPOTENCY_CONFLICT`, and so does a key already used by an inventory operation.
  - A new key after success gets `VERSION_CONFLICT`.
- Resolve (`completed` with an unresolved investigation only; `version`, optional `round`/`resultId` guards; never while a recount round is open):
  - Requires a `conclusion` and exactly one of `transactionId` and `noAdjustmentNeeded: true`.
  - The transaction must exist (`NOT_FOUND`). It must also be an `adjustment` whose lines are all on the task's bin and product, made strictly after the first unresolved result was submitted, and not linked to another task's result (`TRANSACTION_NOT_ELIGIBLE`).
  - Resolve covers every unresolved result, clears the flag, closes the discrepancy alert with the conclusion and writes one immutable `countResolve` entry. That entry has task, result and transaction references and lists the result IDs.
  - The response is `{task, resolvedResultIds, activityLogId, transactionId?}`.
- Dispositions are derived only from immutable evidence (the task's successful `countConfirm`/`countResolve` log entries):
  - a resolved result is `resolved` (+ `resolutionLogId`);
  - otherwise a confirmed result takes the confirm outcome (`confirmedNoDifference`, `adjusted` + its own transaction, `underInvestigation`);
  - otherwise the result is `pending` (the latest result of a pending task, also during a recount from pending) or `superseded`.
  - A zero-difference recount never resolves earlier investigations.
- `allowedActions` for a role with `Inventory.Count`: `submit, cancel` / `confirm, recount` / `recount, resolve`.

## Limitations

- Search (number, bin code, round product name/SKU, Unicode case-insensitive) is applied in memory over the SQL-filtered, newest-first stream.

## Tests

- `tests/Wms.Persistence.Sqlite.Tests/V2/V2CountServiceTests.cs`
- `tests/Wms.Persistence.Sqlite.Tests/V2/V2CountConfirmTests.cs` (B4b)
- `tests/Wms.Api.Tests/V2/V2CountApiTests.cs`

```
dotnet test tests/Wms.Persistence.Sqlite.Tests --filter "FullyQualifiedName~V2CountServiceTests|FullyQualifiedName~V2CountConfirmTests"
dotnet test tests/Wms.Api.Tests --filter FullyQualifiedName~V2CountApiTests
```
