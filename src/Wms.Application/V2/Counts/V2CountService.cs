using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Counts;

/// <summary>
/// Count tasks, B4a (decisions §17; contracts/v2/README.md §9): list, detail, start, submit, recount, cancel.
/// <list type="bullet">
/// <item>Blind: the open round's book quantity is stored in the task's round snapshot and never returned; submitted
/// results are immutable and always show book, actual and difference.</item>
/// <item>Every command runs in the shared writer transaction with the session and current role re-checked
/// (<c>Inventory.Count</c>), then checks <c>version</c>, <c>round</c> = current round and, when sent, <c>resultId</c> =
/// latest result. A submit retry of the current, already submitted round is answered before the version check (same
/// actual and note: 200; otherwise <c>COUNT_ROUND_ALREADY_SUBMITTED</c>), since the first submit changed the version.</item>
/// <item>Rounds only grow; a cancelled round keeps its number and creates no result. Cancel restores round 1 →
/// cancelled (bin released), from pending → pendingConfirmation (bin stays frozen), from completed → completed with the
/// earlier completedAt (bin released).</item>
/// <item>Start requires a UUID <c>Idempotency-Key</c>; key and body digest are stored on the task, a replay returns the
/// task as it is now.</item>
/// <item>B4b: confirm and resolve (see their summaries). Result dispositions are derived from immutable evidence: the
/// task's successful confirm and resolve log entries; a result without one is pending (latest of a pending task) or
/// superseded.</item>
/// </list>
/// </summary>
public sealed class V2CountService(IV2CountReader reader, V2CatalogWriter writer)
{
    public const int MaxText = 2000;
    public const string NumberPrefix = "CT-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Internal marker of an idempotent submit retry (never returned to a caller).
    private static readonly V2Failure NoChange = new("NO_CHANGE", "no change");

    public async Task<V2Result<Page<CountTaskListItem>>> ListAsync(
        string? search, string? status, string? binId, bool? unresolved, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var before, out var size) is { } badPage)
        {
            return badPage;
        }

        if (status is not null && !CountStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            return V2Failure.Validation("status", "status must be inProgress, pendingConfirmation, completed or cancelled.");
        }

        long? bin = null;
        if (!string.IsNullOrEmpty(binId))
        {
            if (V2Text.Id(binId, "binId", out var parsed) is { } badBin)
            {
                return badBin;
            }

            bin = parsed;
        }

        var text = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var page = new List<V2CountTaskListRow>(size + 1);
        await foreach (var row in reader.StreamCountTasksAsync(new V2CountTaskFilter(status, bin, unresolved, before), cancellationToken))
        {
            var t = row.Task;
            if (text is not null && !new[] { t.Number, t.BinCode, t.Round?.ProductName ?? "", t.Round?.Sku ?? "" }
                    .Any(v => V2Text.Normalize(v).Contains(text, StringComparison.Ordinal)))
            {
                continue;
            }

            page.Add(row);
            if (page.Count > size)
            {
                break;
            }
        }

        return new Page<CountTaskListItem>(
            page.Take(size).Select(r => new CountTaskListItem(
                WireId.Format(r.Task.Id), r.Task.Number, WireId.Format(r.Task.BinId), r.Task.BinCode, r.Task.Round?.ProductName ?? string.Empty,
                r.Task.Status, r.Task.CreatorName, r.Task.CreatorRole, r.Task.StartedAt, r.Task.CurrentRound, r.Task.Version,
                r.Task.HasUnresolvedInvestigation, r.Task.CompletedAt, r.LatestResultId is { } latest ? WireId.Format(latest) : null)).ToList(),
            page.Count > size ? V2Paging.Encode(page[size - 1].Task.Id) : null);
    }

    public async Task<V2Result<CountTaskResponse>> GetAsync(V2SessionPrincipal? principal, string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var taskId) is { } invalid)
        {
            return invalid;
        }

        var detail = await reader.FindCountTaskAsync(taskId, cancellationToken);
        return detail is null
            ? V2Failure.NotFound("Count task", V2CatalogMapping.Ref(SubjectTypes.CountTask, taskId))
            : ToResponse(detail, principal?.CurrentRole);
    }

    public async Task<V2Result<CountTaskResponse>> StartAsync(
        V2SessionPrincipal principal, CountStartRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var badKey = ParseKey(idempotencyKey, out var key);
        var badBin = V2Text.Id(request.BinId, "binId", out var binId);
        IReadOnlyList<V2SubjectRecord> subjects = binId > 0 ? [new(SubjectTypes.Bin, binId)] : [];
        if ((badKey ?? badBin) is { } rejected)
        {
            return await writer.RejectAsync<CountTaskResponse>(
                principal, PermissionNames.InventoryCount, ActivityTypes.CountStart, "开始盘点", subjects, rejected, cancellationToken);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] { "countStart/1", binId }, Json))));
        var replayed = false;
        var result = await writer.RunAsync<CountTaskResponse>(
            principal, PermissionNames.InventoryCount, ActivityTypes.CountStart, "开始盘点", subjects,
            async (scope, actor, now) =>
            {
                if (await scope.FindCountTaskByCreationRequestAsync(actor.UserId, key, cancellationToken) is { } earlier)
                {
                    if (!string.Equals(earlier.CreationRequestDigest, digest, StringComparison.Ordinal))
                    {
                        return new V2Failure(
                            ProblemCodesV2.IdempotencyConflict,
                            "This Idempotency-Key was already used for a different count. Use a new key for a new count.",
                            Subjects: [V2CatalogMapping.Ref(SubjectTypes.CountTask, earlier.Id, earlier.Number)]);
                    }

                    replayed = true;
                    return ToResponse((await scope.FindCountTaskDetailAsync(earlier.Id, cancellationToken))!, actor.Role);
                }

                var bin = await scope.FindBinAsync(binId, cancellationToken);
                var binRef = V2CatalogMapping.Ref(SubjectTypes.Bin, binId, bin?.Code);
                if (bin is null)
                {
                    return V2Failure.NotFound("Bin", binRef) with { Field = "binId" };
                }

                if (bin.IsArchived)
                {
                    return new V2Failure(ProblemCodesV2.Archived, "The bin is archived.", "binId", [binRef]);
                }

                if (await scope.FindBlockingCountTaskAsync(binId, cancellationToken) is { } open)
                {
                    return new V2Failure(
                        ProblemCodesV2.CountTaskExists,
                        open.HasUnresolvedInvestigation
                            ? $"Count {open.Number} has an unresolved investigation on this bin; use its recount."
                            : $"Count {open.Number} is already active on this bin.",
                        "binId",
                        [V2CatalogMapping.Ref(SubjectTypes.CountTask, open.Id, open.Number), binRef]);
                }

                var snapshot = await SnapshotAsync(scope, bin, actor, now, cancellationToken);
                if (snapshot is null)
                {
                    return new V2Failure(ProblemCodesV2.CountBinHasNoProduct, "The bin has no current or retained product.", "binId", [binRef]);
                }

                var number = NumberPrefix + (await scope.NextNumberAsync(V2Counter.CountTask, cancellationToken)).ToString("D6", CultureInfo.InvariantCulture);
                var id = await scope.InsertCountTaskAsync(
                    new V2NewCountTask(number, bin.Id, bin.Code, actor.UserId, actor.DisplayName, actor.Role, snapshot, key, digest),
                    cancellationToken);
                await scope.SetBinFrozenAsync(bin.Id, id, now, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CountStart, $"开始盘点 {number}（{bin.Code}）", true,
                    [new(SubjectTypes.CountTask, id, number), new(SubjectTypes.Bin, bin.Id, bin.Code), new(SubjectTypes.Product, snapshot.ProductId, snapshot.Sku)],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [.. RoundChanges(1, snapshot), new("status", null, CountStatuses.InProgress)]));
                return ToResponse((await scope.FindCountTaskDetailAsync(id, cancellationToken))!, actor.Role);
            },
            cancellationToken);

        return result.IsSuccess ? V2Result<CountTaskResponse>.Success(result.Value, replayed) : result;
    }

    public async Task<V2Result<CountTaskResponse>> SubmitAsync(
        V2SessionPrincipal principal, string id, CountSubmitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        long actual = 0;
        var badQuantity = !WireQuantity.TryParse(request.ActualQuantity, QuantitySign.NonNegative, out actual)
            ? V2Failure.Validation("actualQuantity", "actualQuantity must be a non-negative exact decimal string.")
            : null;
        var badNote = note is { Length: > MaxText } ? V2Failure.Validation("note", $"note has at most {MaxText} characters.") : null;
        return await CommandAsync(
            principal, id, ActivityTypes.CountSubmit, "提交盘点", request.Round, request.Version, request.ResultId, badQuantity ?? badNote,
            async (scope, actor, now, detail) =>
            {
                var task = detail.Task;
                var snapshot = task.Round!;
                if (WireQuantity.FractionDigits(actual) > snapshot.DecimalPlaces)
                {
                    return new V2Failure(
                        ProblemCodesV2.PrecisionExceeded,
                        $"Product {snapshot.Sku} allows {snapshot.DecimalPlaces} decimal places; quantities are never rounded.", "actualQuantity");
                }

                var resultId = await scope.InsertCountResultAsync(
                    new V2CountResultRecord(
                        0, task.Id, task.CurrentRound, task.BinId, task.BinCode, snapshot.ProductId, snapshot.ProductName, snapshot.Sku,
                        snapshot.Barcode, snapshot.UnitName, snapshot.DecimalPlaces, snapshot.SerialTracking, snapshot.StartedAt,
                        snapshot.BookQuantityMicros, actual, actual - snapshot.BookQuantityMicros, actor.UserId, actor.DisplayName, actor.Role,
                        now, note, null),
                    cancellationToken);
                if (!await scope.UpdateCountTaskAsync(
                        task.Id, task.Version,
                        new V2CountTaskUpdate(CountStatuses.PendingConfirmation, null, task.CurrentRound, snapshot, null, null, null, null),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CountSubmit, $"提交盘点 {task.Number} 第 {task.CurrentRound} 轮", true,
                    [new(SubjectTypes.CountTask, task.Id, task.Number), new(SubjectTypes.CountResult, resultId), new(SubjectTypes.Bin, task.BinId, task.BinCode),
                        new(SubjectTypes.Product, snapshot.ProductId, snapshot.Sku)],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes:
                    [
                        new("round", null, task.CurrentRound.ToString(CultureInfo.InvariantCulture)),
                        new("bookQuantity", null, WireQuantity.Format(snapshot.BookQuantityMicros)),
                        new("actualQuantity", null, WireQuantity.Format(actual)),
                        new("difference", null, FormatSigned(actual - snapshot.BookQuantityMicros)),
                        new("note", null, note),
                        new("status", task.Status, CountStatuses.PendingConfirmation),
                    ]));
                return null;
            },
            detail =>
            {
                // Retry of the current, already submitted round (decided before the version check; see the summary).
                var task = detail.Task;
                if (request.Round != task.CurrentRound || detail.Results.LastOrDefault() is not { } last || last.Round != request.Round)
                {
                    return null;
                }

                return badQuantity is null && last.ActualQuantityMicros == actual && string.Equals(last.Note, note, StringComparison.Ordinal)
                    ? NoChange
                    : new V2Failure(
                        ProblemCodesV2.CountRoundAlreadySubmitted,
                        $"Round {request.Round} was already submitted with other values.",
                        Subjects: [TaskRef(task), V2CatalogMapping.Ref(SubjectTypes.CountResult, last.Id)]);
            },
            CountStatuses.InProgress,
            cancellationToken);
    }

    public async Task<V2Result<CountTaskResponse>> RecountAsync(
        V2SessionPrincipal principal, string id, CountRecountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badReason = Reason(request.Reason, out var reason);
        return await CommandAsync(
            principal, id, ActivityTypes.CountRecount, "复盘", request.Round, request.Version, request.ResultId, badReason,
            async (scope, actor, now, detail) =>
            {
                var task = detail.Task;
                var fromCompleted = task.Status == CountStatuses.Completed;
                if (fromCompleted && !task.HasUnresolvedInvestigation)
                {
                    return StateConflict(task, "A completed count can be recounted only while its investigation is unresolved.");
                }

                var bin = (await scope.FindBinAsync(task.BinId, cancellationToken))!;
                var snapshot = await SnapshotAsync(scope, bin, actor, now, cancellationToken);
                if (snapshot is null)
                {
                    return new V2Failure(
                        ProblemCodesV2.CountBinHasNoProduct, "The bin has no current or retained product.",
                        Subjects: [TaskRef(task), V2CatalogMapping.Ref(SubjectTypes.Bin, bin.Id, bin.Code)]);
                }

                var round = task.CurrentRound + 1;
                if (!await scope.UpdateCountTaskAsync(
                        task.Id, task.Version,
                        new V2CountTaskUpdate(
                            CountStatuses.InProgress, null, round, snapshot, task.Status, fromCompleted ? task.CompletedAt : null, null, null),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                if (bin.FrozenCountTaskId != task.Id)
                {
                    await scope.SetBinFrozenAsync(bin.Id, task.Id, now, cancellationToken);
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CountRecount, $"复盘 {task.Number} 第 {round} 轮", true,
                    EventSubjects(detail, snapshot),
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [.. RoundChanges(round, snapshot), new("status", task.Status, CountStatuses.InProgress), new("reason", null, reason)]));
                return null;
            },
            _ => null,
            null,
            cancellationToken);
    }

    public async Task<V2Result<CountTaskResponse>> CancelAsync(
        V2SessionPrincipal principal, string id, CountCancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badReason = Reason(request.Reason, out var reason);
        return await CommandAsync(
            principal, id, ActivityTypes.CountCancel, "取消盘点", request.Round, request.Version, request.ResultId, badReason,
            async (scope, actor, now, detail) =>
            {
                var task = detail.Task;
                var snapshot = task.Round!;
                string status = CountStatuses.Cancelled;
                DateTimeOffset? completedAt = null;
                DateTimeOffset? cancelledAt = now;
                string? cancelReason = reason;
                var release = true;
                if (task.StatusBeforeRound == CountStatuses.PendingConfirmation)
                {
                    (status, cancelledAt, cancelReason, release) = (CountStatuses.PendingConfirmation, null, null, false);
                }
                else if (task.StatusBeforeRound == CountStatuses.Completed)
                {
                    (status, completedAt, cancelledAt, cancelReason) = (CountStatuses.Completed, task.CompletedAtBeforeRound, null, null);
                }

                // The round number is kept (never decremented); the cancelled snapshot stays only in the log.
                if (!await scope.UpdateCountTaskAsync(
                        task.Id, task.Version,
                        new V2CountTaskUpdate(status, completedAt, task.CurrentRound, snapshot, null, null, cancelledAt, cancelReason),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                if (release)
                {
                    await scope.SetBinFrozenAsync(task.BinId, null, now, cancellationToken);
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.CountCancel, $"取消盘点 {task.Number} 第 {task.CurrentRound} 轮", true,
                    EventSubjects(detail, snapshot),
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [.. RoundChanges(task.CurrentRound, snapshot), new("status", task.Status, status), new("reason", null, reason)]));
                return null;
            },
            _ => null,
            CountStatuses.InProgress,
            cancellationToken);
    }

    /// <summary>
    /// Confirms the latest result of a pending task in one writer transaction (decisions §17.1, §17.5):
    /// zero difference completes without a transaction; an untracked product with a difference (reason required) sets
    /// the frozen bin to the actual quantity and writes an adjustment linked to the result, with capacity alerts; a
    /// serial-tracked difference changes no stock or item, keeps the investigation open and opens the task's one
    /// discrepancy alert (linked to the first unresolved result) when none is open. The task completes and the bin is
    /// released. Replay: the actor's key and body digest are stored in the confirm log entry (and on the adjustment).
    /// </summary>
    public async Task<V2Result<CountTaskResponse>> ConfirmAsync(
        V2SessionPrincipal principal, string id, CountConfirmRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var rejected = ParseKey(idempotencyKey, out var key)
            ?? (string.IsNullOrEmpty(request.ResultId) ? V2Failure.Validation("resultId", "resultId (the latest result) is required.") : null)
            ?? (reason is { Length: > MaxText } ? V2Failure.Validation("reason", $"reason has at most {MaxText} characters.") : null);
        if (rejected is not null)
        {
            IReadOnlyList<V2SubjectRecord> subjects = V2Text.Id(id, "id", out var taskId) is null ? [new(SubjectTypes.CountTask, taskId)] : [];
            return await writer.RejectAsync<CountTaskResponse>(
                principal, PermissionNames.InventoryCount, ActivityTypes.CountConfirm, "确认盘点", subjects, rejected, cancellationToken);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object?[] { "countConfirm/1", id.Trim(), request.ResultId!.Trim(), request.Round, request.Version, reason }, Json))));
        var replayed = false;
        var result = await CommandAsync(
            principal, id, ActivityTypes.CountConfirm, "确认盘点", request.Round, request.Version, request.ResultId, null,
            async (scope, actor, now, detail) =>
            {
                var task = detail.Task;
                var latest = detail.Results[^1];
                var subjects = new List<V2SubjectRecord>
                {
                    new(SubjectTypes.CountTask, task.Id, task.Number), new(SubjectTypes.Bin, task.BinId, task.BinCode),
                    new(SubjectTypes.Product, latest.ProductId, latest.Sku), new(SubjectTypes.CountResult, latest.Id),
                };
                var diff = latest.DifferenceMicros;
                var outcome = diff == 0
                    ? CountResultDispositions.ConfirmedNoDifference
                    : latest.SerialTracking ? CountResultDispositions.UnderInvestigation : CountResultDispositions.Adjusted;
                if (outcome == CountResultDispositions.Adjusted && reason is null)
                {
                    return V2Failure.Validation("reason", "reason is required to confirm a difference of an untracked product.");
                }

                if (!await scope.UpdateCountTaskAsync(
                        task.Id, task.Version,
                        new V2CountTaskUpdate(
                            CountStatuses.Completed, now, task.CurrentRound, task.Round, null, null, null, null,
                            outcome == CountResultDispositions.UnderInvestigation ? true : null),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                await scope.SetBinFrozenAsync(task.BinId, null, now, cancellationToken);
                long? transactionId = null;
                if (outcome == CountResultDispositions.Adjusted)
                {
                    // The bin was frozen since the round started, so the result's book is still the bin's quantity.
                    var bin = (await scope.FindBinAsync(task.BinId, cancellationToken))!;
                    if (bin.QuantityMicros != latest.BookQuantityMicros || bin.ProductId != latest.ProductId
                        || !await scope.UpdateBinStockAsync(
                            new V2BinStockUpdate(
                                bin.Id, bin.Version, bin.ProductId, latest.ActualQuantityMicros, bin.CapacityMicros, bin.LowThresholdMicros,
                                bin.HighThresholdMicros),
                            now, cancellationToken))
                    {
                        return new V2Failure(ProblemCodesV2.ConcurrencyConflict, "The bin changed underneath this confirmation; nothing was committed.");
                    }

                    var number = "TX-" + (await scope.NextNumberAsync(V2Counter.Transaction, cancellationToken)).ToString("D6", CultureInfo.InvariantCulture);
                    transactionId = await scope.InsertTransactionAsync(
                        new V2NewTransaction(
                            number, TransactionTypesV2.Adjustment, actor.UserId, actor.DisplayName, actor.Role, now, null, null, reason, latest.Id, key, digest,
                            [new V2TransactionLineRecord(
                                1, latest.ProductId, latest.ProductName, latest.Sku, latest.Barcode, latest.UnitName, latest.DecimalPlaces, diff,
                                null, null, null, null, bin.Id, bin.Code, latest.BookQuantityMicros, latest.ActualQuantityMicros, null, null, null, [])]),
                        cancellationToken);
                    var settings = await scope.GetSettingsAsync(cancellationToken);
                    var alerts = await V2CapacityAlerts.ReconcileAsync(
                        scope, [bin.Id], settings.LowThresholdMicros, settings.HighThresholdMicros, now, number, cancellationToken);
                    subjects.Add(new V2SubjectRecord(SubjectTypes.Transaction, transactionId.Value, number));
                    subjects.AddRange(alerts.Subjects);
                }
                else if (outcome == CountResultDispositions.UnderInvestigation
                    && await scope.FindOpenDiscrepancyAlertAsync(task.Id, cancellationToken) is null)
                {
                    var first = UnresolvedResults(detail).Append(latest).OrderBy(r => r.Round).First();
                    var alertId = await scope.InsertDiscrepancyAlertAsync(
                        new V2NewDiscrepancyAlert(
                            task.Id, first.Id, task.BinId, first.ProductId,
                            $"{task.BinCode} 盘点差异待调查：账面 {WireQuantity.Format(first.BookQuantityMicros)}，实盘 {WireQuantity.Format(first.ActualQuantityMicros)}",
                            JsonSerializer.Serialize(
                                new
                                {
                                    round = first.Round,
                                    bookQuantity = WireQuantity.Format(first.BookQuantityMicros),
                                    actualQuantity = WireQuantity.Format(first.ActualQuantityMicros),
                                    difference = WireQuantity.Format(first.DifferenceMicros),
                                    sku = first.Sku,
                                    unitName = first.UnitName,
                                    decimalPlaces = first.DecimalPlaces,
                                },
                                Json),
                            now),
                        cancellationToken);
                    subjects.Add(new V2SubjectRecord(SubjectTypes.Alert, alertId));
                }

                await scope.AddCountEvidenceAsync(
                    new V2ActivityEntry(
                        now, ActivityTypes.CountConfirm, $"确认盘点 {task.Number} 第 {latest.Round} 轮结果", true, subjects,
                        actor.UserId, actor.DisplayName, actor.Role,
                        Changes:
                        [
                            new("resultId", null, WireId.Format(latest.Id)),
                            new("outcome", null, outcome),
                            new("transactionId", null, transactionId is { } tx ? WireId.Format(tx) : null),
                            new("round", null, latest.Round.ToString(CultureInfo.InvariantCulture)),
                            new("bookQuantity", null, WireQuantity.Format(latest.BookQuantityMicros)),
                            new("actualQuantity", null, WireQuantity.Format(latest.ActualQuantityMicros)),
                            new("difference", null, WireQuantity.Format(diff)),
                            new("reason", null, reason),
                            new("status", task.Status, CountStatuses.Completed),
                            new("requestKey", null, key),
                            new("requestDigest", null, digest),
                        ]),
                    task.Id, latest.Id, transactionId, cancellationToken);
                return null;
            },
            _ => null,
            CountStatuses.PendingConfirmation,
            cancellationToken,
            async (scope, actor, detail) =>
            {
                if (await scope.FindCountConfirmByRequestAsync(actor.UserId, key, cancellationToken) is { } earlier)
                {
                    if (!string.Equals(earlier.RequestDigest, digest, StringComparison.Ordinal))
                    {
                        return new V2Failure(
                            ProblemCodesV2.IdempotencyConflict, "This Idempotency-Key was already used for a different confirmation.");
                    }

                    replayed = true;
                    return NoChange;
                }

                return await scope.FindTransactionByRequestAsync(actor.UserId, key, cancellationToken) is null
                    ? null
                    : new V2Failure(ProblemCodesV2.IdempotencyConflict, "This Idempotency-Key was already used for another operation.");
            });
        return result.IsSuccess ? V2Result<CountTaskResponse>.Success(result.Value, replayed) : result;
    }

    /// <summary>
    /// Closes the investigation of a completed task (decisions §17.5): a conclusion and exactly one of an eligible
    /// committed adjustment (same bin and product, after the first unresolved result) and <c>noAdjustmentNeeded</c>.
    /// Covers every unresolved result, writes one immutable log entry listing them and closes the discrepancy alert.
    /// </summary>
    public async Task<V2Result<CountResolutionResponse>> ResolveAsync(
        V2SessionPrincipal principal, string id, CountResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var taskId);
        var conclusion = (request.Conclusion ?? string.Empty).Trim();
        long? transactionId = null;
        long? expectedResult = null;
        V2Failure? badTransaction = null;
        V2Failure? badResult = null;
        if (!string.IsNullOrEmpty(request.TransactionId))
        {
            badTransaction = V2Text.Id(request.TransactionId, "transactionId", out var tx);
            transactionId = tx;
        }

        if (!string.IsNullOrEmpty(request.ResultId))
        {
            badResult = V2Text.Id(request.ResultId, "resultId", out var parsed);
            expectedResult = parsed;
        }

        var rejected = badId
            ?? (conclusion.Length == 0 || conclusion.Length > MaxText
                ? V2Failure.Validation("conclusion", $"conclusion is required (at most {MaxText} characters).") : null)
            ?? ((transactionId is null) == !request.NoAdjustmentNeeded && badTransaction is null
                ? V2Failure.Validation("transactionId", "Send exactly one of transactionId and noAdjustmentNeeded: true.") : null)
            ?? badTransaction
            ?? badResult
            ?? (request.Version < 1 ? V2Failure.Validation("version", "The current version of the task is required.") : null)
            ?? (request.Round is < 1 ? V2Failure.Validation("round", "round must be positive when sent.") : null);
        IReadOnlyList<V2SubjectRecord> requestSubjects = taskId > 0 ? [new(SubjectTypes.CountTask, taskId)] : [];
        if (rejected is not null)
        {
            return await writer.RejectAsync<CountResolutionResponse>(
                principal, PermissionNames.InventoryCount, ActivityTypes.CountResolve, "盘点调查结案", requestSubjects, rejected, cancellationToken);
        }

        return await writer.RunAsync<CountResolutionResponse>(
            principal, PermissionNames.InventoryCount, ActivityTypes.CountResolve, "盘点调查结案", requestSubjects,
            async (scope, actor, now) =>
            {
                var detail = await scope.FindCountTaskDetailAsync(taskId, cancellationToken);
                if (detail is null)
                {
                    return V2Failure.NotFound("Count task", V2CatalogMapping.Ref(SubjectTypes.CountTask, taskId));
                }

                var task = detail.Task;
                if (task.Version != request.Version)
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                if (request.Round is { } round && round != task.CurrentRound)
                {
                    return StateConflict(task, $"round {round} is not the current round {task.CurrentRound}.");
                }

                if (expectedResult is { } expected && detail.Results.LastOrDefault()?.Id != expected)
                {
                    return StateConflict(task, "resultId is not the latest result of the task.");
                }

                var unresolved = UnresolvedResults(detail);
                if (task.Status != CountStatuses.Completed || !task.HasUnresolvedInvestigation || unresolved.Count == 0)
                {
                    return StateConflict(task, "Only a completed task with an unresolved investigation (no open recount round) can be resolved.");
                }

                var first = unresolved[0];
                var subjects = new List<V2SubjectRecord>
                {
                    new(SubjectTypes.CountTask, task.Id, task.Number), new(SubjectTypes.Bin, task.BinId, task.BinCode),
                    new(SubjectTypes.Product, first.ProductId, first.Sku),
                };
                subjects.AddRange(unresolved.Select(r => new V2SubjectRecord(SubjectTypes.CountResult, r.Id)));
                if (transactionId is { } txId)
                {
                    var transaction = await scope.FindTransactionAsync(txId, cancellationToken);
                    if (transaction is null)
                    {
                        return V2Failure.NotFound("Transaction", V2CatalogMapping.Ref(SubjectTypes.Transaction, txId)) with { Field = "transactionId" };
                    }

                    var ownResults = detail.Results.Select(r => r.Id).ToHashSet();
                    var eligible = transaction.Type == TransactionTypesV2.Adjustment
                        && transaction.Lines.Count > 0
                        && transaction.Lines.All(l => l.ToBinId == task.BinId && l.ProductId == first.ProductId)
                        && transaction.OccurredAt > first.SubmittedAt
                        && (transaction.CountResultId is null || ownResults.Contains(transaction.CountResultId.Value));
                    if (!eligible)
                    {
                        return new V2Failure(
                            ProblemCodesV2.TransactionNotEligible,
                            "The transaction must be a committed adjustment of the task's bin and product made after the first unresolved result.",
                            "transactionId",
                            [V2CatalogMapping.Ref(SubjectTypes.Transaction, txId, transaction.Number), TaskRef(task)]);
                    }

                    subjects.Add(new V2SubjectRecord(SubjectTypes.Transaction, txId, transaction.Number));
                }

                if (!await scope.UpdateCountTaskAsync(
                        task.Id, task.Version,
                        new V2CountTaskUpdate(
                            task.Status, task.CompletedAt, task.CurrentRound, task.Round, null, null, task.CancelledAt, task.CancelReason, false),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                if (await scope.FindOpenDiscrepancyAlertAsync(task.Id, cancellationToken) is { } alertId)
                {
                    await scope.ResolveAlertAsync(alertId, now, conclusion, cancellationToken);
                    subjects.Add(new V2SubjectRecord(SubjectTypes.Alert, alertId));
                }

                var resolvedIds = unresolved.Select(r => r.Id).ToList();
                var logId = await scope.AddCountEvidenceAsync(
                    new V2ActivityEntry(
                        now, ActivityTypes.CountResolve, $"盘点调查结案 {task.Number}：{resolvedIds.Count} 个结果", true, subjects,
                        actor.UserId, actor.DisplayName, actor.Role,
                        Changes:
                        [
                            new("resolvedResultIds", null, string.Join(',', resolvedIds.Select(r => WireId.Format(r)))),
                            new("conclusion", null, conclusion),
                            new("noAdjustmentNeeded", null, request.NoAdjustmentNeeded ? "true" : "false"),
                            new("transactionId", null, transactionId is { } t ? WireId.Format(t) : null),
                            new("hasUnresolvedInvestigation", "true", "false"),
                        ]),
                    task.Id, first.Id, transactionId, cancellationToken);
                var fresh = (await scope.FindCountTaskDetailAsync(task.Id, cancellationToken))!;
                return new CountResolutionResponse(
                    ToResponse(fresh, actor.Role), resolvedIds.Select(r => WireId.Format(r)).ToList(), WireId.Format(logId),
                    transactionId is { } done ? WireId.Format(done) : null);
            },
            cancellationToken);
    }

    /// <summary>Results confirmed as serial differences and not yet covered by a resolve, by round.</summary>
    private static List<V2CountResultRecord> UnresolvedResults(V2CountTaskDetail detail)
    {
        var (confirms, resolvedBy) = Evidence(detail);
        return detail.Results
            .Where(r => confirms.TryGetValue(r.Id, out var c) && c.Outcome == CountResultDispositions.UnderInvestigation && !resolvedBy.ContainsKey(r.Id))
            .OrderBy(r => r.Round)
            .ToList();
    }

    private static (Dictionary<long, V2CountEvidence> Confirms, Dictionary<long, long> ResolvedBy) Evidence(V2CountTaskDetail detail)
    {
        var confirms = new Dictionary<long, V2CountEvidence>();
        var resolvedBy = new Dictionary<long, long>();
        foreach (var e in detail.Evidence ?? [])
        {
            if (e.Type == ActivityTypes.CountConfirm && e.ResultId is { } result)
            {
                confirms[result] = e;
            }
            else if (e.Type == ActivityTypes.CountResolve)
            {
                foreach (var covered in e.ResolvedResultIds)
                {
                    resolvedBy.TryAdd(covered, e.LogId);
                }
            }
        }

        return (confirms, resolvedBy);
    }

    /// <summary>
    /// Shared command frame: request validation (logged rejection), task lookup, the optional pre-version rule, then
    /// version, round, resultId and status guards, the work, and the committed task read back.
    /// </summary>
    private async Task<V2Result<CountTaskResponse>> CommandAsync(
        V2SessionPrincipal principal,
        string id,
        string activityType,
        string description,
        int round,
        int version,
        string? resultId,
        V2Failure? invalid,
        Func<IV2CatalogWriteScope, V2Actor, DateTimeOffset, V2CountTaskDetail, Task<V2Failure?>> work,
        Func<V2CountTaskDetail, V2Failure?> beforeVersion,
        string? requiredStatus,
        CancellationToken cancellationToken,
        Func<IV2CatalogWriteScope, V2Actor, V2CountTaskDetail, Task<V2Failure?>>? replay = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var badId = V2Text.Id(id, "id", out var taskId);
        long? expectedResult = null;
        V2Failure? badResult = null;
        if (!string.IsNullOrEmpty(resultId))
        {
            badResult = V2Text.Id(resultId, "resultId", out var parsed);
            expectedResult = parsed;
        }

        var rejected = badId
            ?? (version < 1 ? V2Failure.Validation("version", "The current version of the task is required.") : null)
            ?? (round < 1 ? V2Failure.Validation("round", "The current round of the task is required.") : null)
            ?? badResult;
        IReadOnlyList<V2SubjectRecord> subjects = taskId > 0 ? [new(SubjectTypes.CountTask, taskId)] : [];
        if (rejected is not null)
        {
            return await writer.RejectAsync<CountTaskResponse>(
                principal, PermissionNames.InventoryCount, activityType, description, subjects, rejected, cancellationToken);
        }

        var result = await writer.RunAsync<CountTaskResponse>(
            principal, PermissionNames.InventoryCount, activityType, description, subjects,
            async (scope, actor, now) =>
            {
                var detail = await scope.FindCountTaskDetailAsync(taskId, cancellationToken);
                if (detail is null)
                {
                    return V2Failure.NotFound("Count task", V2CatalogMapping.Ref(SubjectTypes.CountTask, taskId));
                }

                var task = detail.Task;
                var early = beforeVersion(detail);
                if (early is null && replay is not null)
                {
                    early = await replay(scope, actor, detail); // after the fresh current-role authorization
                }

                if (early is not null)
                {
                    return ReferenceEquals(early, NoChange) ? ToResponse(detail, actor.Role) : early;
                }

                if (invalid is not null)
                {
                    return invalid;
                }

                if (task.Version != version)
                {
                    return V2Failure.VersionConflict(TaskRef(task));
                }

                if (round != task.CurrentRound)
                {
                    return StateConflict(task, $"round {round} is not the current round {task.CurrentRound}.");
                }

                if (expectedResult is { } expected && detail.Results.LastOrDefault()?.Id != expected)
                {
                    return StateConflict(task, "resultId is not the latest result of the task.");
                }

                var allowed = requiredStatus is null
                    ? task.Status is CountStatuses.PendingConfirmation or CountStatuses.Completed
                    : task.Status == requiredStatus;
                if (!allowed)
                {
                    return StateConflict(task, $"The command is not allowed while the task is {task.Status}.");
                }

                if (await work(scope, actor, now, detail) is { } failure)
                {
                    return failure;
                }

                return ToResponse((await scope.FindCountTaskDetailAsync(taskId, cancellationToken))!, actor.Role);
            },
            cancellationToken);
        return result;
    }

    private static async Task<V2CountRoundSnapshot?> SnapshotAsync(
        IV2CatalogWriteScope scope, V2BinRecord bin, V2Actor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (bin.ProductId is not { } productId
            || !(await scope.FindProductRecordsAsync([productId], cancellationToken)).TryGetValue(productId, out var product))
        {
            return null;
        }

        return new V2CountRoundSnapshot(
            now, actor.UserId, actor.DisplayName, actor.Role, product.Id, product.Name, product.Sku, product.Barcode, product.UnitName,
            product.DecimalPlaces, product.SerialTracking, bin.QuantityMicros);
    }

    /// <summary>Task, bin, the round snapshot's product and, when present, the latest result.</summary>
    private static List<V2SubjectRecord> EventSubjects(V2CountTaskDetail detail, V2CountRoundSnapshot snapshot)
    {
        var task = detail.Task;
        var subjects = new List<V2SubjectRecord>
        {
            new(SubjectTypes.CountTask, task.Id, task.Number),
            new(SubjectTypes.Bin, task.BinId, task.BinCode),
            new(SubjectTypes.Product, snapshot.ProductId, snapshot.Sku),
        };
        if (detail.Results.LastOrDefault() is { } latest)
        {
            subjects.Add(new V2SubjectRecord(SubjectTypes.CountResult, latest.Id));
        }

        return subjects;
    }

    private static List<V2FieldChange> RoundChanges(int round, V2CountRoundSnapshot s) =>
    [
        new("round", null, round.ToString(CultureInfo.InvariantCulture)),
        new("productId", null, WireId.Format(s.ProductId)),
        new("sku", null, s.Sku),
        new("bookQuantity", null, WireQuantity.Format(s.BookQuantityMicros)),
        new("decimalPlaces", null, s.DecimalPlaces.ToString(CultureInfo.InvariantCulture)),
    ];

    internal static CountTaskResponse ToResponse(V2CountTaskDetail detail, string? role)
    {
        var task = detail.Task;
        var results = detail.Results;
        var latestPending = task.Status == CountStatuses.PendingConfirmation
            || (task.Status == CountStatuses.InProgress && task.StatusBeforeRound == CountStatuses.PendingConfirmation);
        var actions = new List<string>();
        if (role is not null && V2RolePermissions.Grants(role, PermissionNames.InventoryCount))
        {
            if (task.Status == CountStatuses.InProgress)
            {
                actions.Add(CountActions.Submit);
                actions.Add(CountActions.Cancel);
            }
            else if (task.Status == CountStatuses.PendingConfirmation)
            {
                actions.Add(CountActions.Confirm);
                actions.Add(CountActions.Recount);
            }
            else if (task.Status == CountStatuses.Completed && task.HasUnresolvedInvestigation)
            {
                actions.Add(CountActions.Recount);
                actions.Add(CountActions.Resolve);
            }
        }

        var (confirms, resolvedBy) = Evidence(detail);
        var open = task.Status == CountStatuses.InProgress ? task.Round : null;
        return new CountTaskResponse(
            WireId.Format(task.Id), task.Number, WireId.Format(task.BinId), task.BinCode, task.Status, WireId.Format(task.CreatorId),
            task.CreatorName, task.CreatorRole, task.StartedAt, task.CurrentRound, task.Version, task.HasUnresolvedInvestigation,
            results.Select((r, i) =>
            {
                // Immutable evidence first: a resolve, then the confirm outcome; otherwise pending (latest) or superseded.
                if (resolvedBy.TryGetValue(r.Id, out var logId))
                {
                    return ToResult(r, CountResultDispositions.Resolved, null, logId);
                }

                if (confirms.TryGetValue(r.Id, out var confirm))
                {
                    return ToResult(r, confirm.Outcome ?? CountResultDispositions.Pending, confirm.TransactionId, null);
                }

                return ToResult(
                    r, i == results.Count - 1 && latestPending ? CountResultDispositions.Pending : CountResultDispositions.Superseded, null, null);
            }).ToList(),
            actions,
            task.CompletedAt,
            results.Count > 0 ? WireId.Format(results[^1].Id) : null,
            open is null
                ? null
                : new CountRoundInfo(
                    task.CurrentRound, open.StartedAt, WireId.Format(open.StartedById), open.StartedByName, open.StartedByRole, task.BinCode,
                    WireId.Format(open.ProductId), open.ProductName, open.Sku, open.UnitName, open.DecimalPlaces, open.SerialTracking),
            task.Status == CountStatuses.InProgress ? task.StatusBeforeRound : null,
            task.CancelledAt,
            task.CancelReason);
    }

    private static CountResult ToResult(V2CountResultRecord r, string disposition, long? adjustment, long? resolutionLog) => new(
        WireId.Format(r.Id), r.Round, WireId.Format(r.BinId), r.BinCode, WireId.Format(r.ProductId), r.ProductName, r.Sku, r.Barcode,
        r.UnitName, r.DecimalPlaces, r.SerialTracking, r.RoundStartedAt, WireQuantity.Format(r.BookQuantityMicros),
        WireQuantity.Format(r.ActualQuantityMicros), FormatSigned(r.DifferenceMicros), WireId.Format(r.SubmittedById), r.SubmittedByName,
        r.SubmittedByRole, r.SubmittedAt, disposition, r.Note,
        adjustment is { } tx ? WireId.Format(tx) : null,
        resolutionLog is { } log ? WireId.Format(log) : null);

    private static string FormatSigned(long micros) => WireQuantity.Format(micros);

    private static SubjectRef TaskRef(V2CountTaskRecord task) => V2CatalogMapping.Ref(SubjectTypes.CountTask, task.Id, task.Number);

    private static V2Failure StateConflict(V2CountTaskRecord task, string message) =>
        new(ProblemCodesV2.CountStateConflict, message, Subjects: [TaskRef(task)]);

    private static V2Failure? Reason(string? value, out string reason)
    {
        reason = (value ?? string.Empty).Trim();
        return reason.Length == 0 || reason.Length > MaxText
            ? V2Failure.Validation("reason", $"reason is required (at most {MaxText} characters).")
            : null;
    }

    private static V2Failure? ParseKey(string? header, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(header) || !Guid.TryParseExact(header.Trim(), "D", out var guid))
        {
            return V2Failure.Validation("Idempotency-Key", "The Idempotency-Key header is required and must be a UUID.");
        }

        key = guid.ToString("D");
        return null;
    }
}
