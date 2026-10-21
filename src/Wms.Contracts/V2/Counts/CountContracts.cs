namespace Wms.Contracts.V2.Counts;

/// <summary><c>POST /api/v2/counts</c>: starts round 1 on a bin and freezes it.</summary>
public sealed record CountStartRequest(string BinId);

/// <summary>
/// The open round while a task is <c>inProgress</c>. Blind count: it deliberately has no book quantity; the persisted
/// book snapshot is revealed only in the immutable <see cref="CountResult"/> created by the submit.
/// </summary>
public sealed record CountRoundInfo(
    int Round,
    DateTimeOffset StartedAt,
    string StartedById,
    string StartedByName,
    string StartedByRole,
    string BinCode,
    string ProductId,
    string ProductName,
    string Sku,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking);

/// <summary>
/// One immutable submitted round. All fields except <see cref="Disposition"/>, <see cref="AdjustmentTransactionId"/> and
/// <see cref="ResolutionLogId"/> are stored facts; those three are derived from later transactions and logs.
/// </summary>
public sealed record CountResult(
    string Id,
    int Round,
    string BinId,
    string BinCode,
    string ProductId,
    string ProductName,
    string Sku,
    string Barcode,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    DateTimeOffset RoundStartedAt,
    string BookQuantity,
    string ActualQuantity,
    string Difference,
    string SubmittedById,
    string SubmittedByName,
    string SubmittedByRole,
    DateTimeOffset SubmittedAt,
    string Disposition,
    string? Note = null,
    string? AdjustmentTransactionId = null,
    string? ResolutionLogId = null);

/// <summary><c>GET /api/v2/counts</c> row.</summary>
public sealed record CountTaskListItem(
    string Id,
    string Number,
    string BinId,
    string BinCode,
    string ProductName,
    string Status,
    string CreatorName,
    string CreatorRole,
    DateTimeOffset StartedAt,
    int Round,
    int Version,
    bool HasUnresolvedInvestigation,
    DateTimeOffset? CompletedAt = null,
    string? LatestResultId = null);

/// <summary>
/// <c>GET /api/v2/counts/{id}</c>, also returned by every count command. <see cref="Round"/> is the number of the latest
/// started round (cancelled rounds keep their number). <see cref="LatestResultId"/> is the newest submitted result.
/// <see cref="CurrentRound"/> and <see cref="StatusBeforeRound"/> are present only while <c>inProgress</c>;
/// <see cref="StatusBeforeRound"/> is the status a cancel restores (absent for round 1, whose cancel ends the task).
/// <see cref="AllowedActions"/> lists the commands the current session role may send now.
/// </summary>
public sealed record CountTaskResponse(
    string Id,
    string Number,
    string BinId,
    string BinCode,
    string Status,
    string CreatorId,
    string CreatorName,
    string CreatorRole,
    DateTimeOffset StartedAt,
    int Round,
    int Version,
    bool HasUnresolvedInvestigation,
    IReadOnlyList<CountResult> Results,
    IReadOnlyList<string> AllowedActions,
    DateTimeOffset? CompletedAt = null,
    string? LatestResultId = null,
    CountRoundInfo? CurrentRound = null,
    string? StatusBeforeRound = null,
    DateTimeOffset? CancelledAt = null,
    string? CancelReason = null);

/// <summary><c>POST /api/v2/counts/{id}/submit</c>. <see cref="ResultId"/>, when sent, must equal the task's latest result.</summary>
public sealed record CountSubmitRequest(
    string ActualQuantity,
    int Round,
    int Version,
    string? Note = null,
    string? ResultId = null);

/// <summary><c>POST /api/v2/counts/{id}/confirm</c> (header <c>Idempotency-Key</c> required): acts on <see cref="ResultId"/>, which must be the latest result.</summary>
public sealed record CountConfirmRequest(string ResultId, int Round, int Version, string? Reason = null);

/// <summary><c>POST /api/v2/counts/{id}/recount</c>: opens round <see cref="Round"/> + 1.</summary>
public sealed record CountRecountRequest(string Reason, int Round, int Version, string? ResultId = null);

/// <summary><c>POST /api/v2/counts/{id}/cancel</c>: cancels the open, unsubmitted round <see cref="Round"/>.</summary>
public sealed record CountCancelRequest(string Reason, int Round, int Version, string? ResultId = null);

/// <summary>
/// <c>POST /api/v2/counts/{id}/resolve</c>: closes the investigation of a <c>completed</c> task. Exactly one of
/// <see cref="TransactionId"/> (an eligible adjustment) and <see cref="NoAdjustmentNeeded"/> = true.
/// </summary>
public sealed record CountResolveRequest(
    string Conclusion,
    bool NoAdjustmentNeeded,
    int Version,
    string? TransactionId = null,
    string? ResultId = null,
    int? Round = null);

/// <summary>Result of a resolve: every unresolved result it covered, and the immutable log entry recording it.</summary>
public sealed record CountResolutionResponse(
    CountTaskResponse Task,
    IReadOnlyList<string> ResolvedResultIds,
    string ActivityLogId,
    string? TransactionId = null);

public static class CountStatuses
{
    public const string InProgress = "inProgress";
    public const string PendingConfirmation = "pendingConfirmation";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [InProgress, PendingConfirmation, Completed, Cancelled];
}

public static class CountActions
{
    public const string Submit = "submit";
    public const string Cancel = "cancel";
    public const string Confirm = "confirm";
    public const string Recount = "recount";
    public const string Resolve = "resolve";

    public static IReadOnlyList<string> All { get; } = [Submit, Cancel, Confirm, Recount, Resolve];
}

/// <summary>Derived state of one submitted result.</summary>
public static class CountResultDispositions
{
    /// <summary>Latest result, awaiting confirm or recount.</summary>
    public const string Pending = "pending";

    /// <summary>A later round was submitted before this one was confirmed; kept as evidence.</summary>
    public const string Superseded = "superseded";

    /// <summary>Confirmed with zero difference; no transaction.</summary>
    public const string ConfirmedNoDifference = "confirmedNoDifference";

    /// <summary>Confirmed; the stock was adjusted by <see cref="CountResult.AdjustmentTransactionId"/>.</summary>
    public const string Adjusted = "adjusted";

    /// <summary>Serial-tracked difference (or evidence round of such a task) awaiting a resolve.</summary>
    public const string UnderInvestigation = "underInvestigation";

    /// <summary>Covered by a resolve, see <see cref="CountResult.ResolutionLogId"/>.</summary>
    public const string Resolved = "resolved";

    public static IReadOnlyList<string> All { get; } =
        [Pending, Superseded, ConfirmedNoDifference, Adjusted, UnderInvestigation, Resolved];
}
