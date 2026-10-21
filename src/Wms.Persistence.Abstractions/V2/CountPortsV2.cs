namespace Wms.Persistence.Abstractions.V2;

/// <summary>The frozen snapshot of the open round (book quantity included; never sent while the round is open).</summary>
public sealed record V2CountRoundSnapshot(
    DateTimeOffset StartedAt,
    long StartedById,
    string StartedByName,
    string StartedByRole,
    long ProductId,
    string ProductName,
    string Sku,
    string Barcode,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    long BookQuantityMicros);

/// <summary>A count task row (B4a). <see cref="Round"/> is set while a round snapshot is stored.</summary>
public sealed record V2CountTaskRecord(
    long Id,
    string Number,
    long BinId,
    string BinCode,
    long CreatorId,
    string CreatorName,
    string CreatorRole,
    DateTimeOffset StartedAt,
    string Status,
    DateTimeOffset? CompletedAt,
    int CurrentRound,
    bool HasUnresolvedInvestigation,
    V2CountRoundSnapshot? Round,
    string? StatusBeforeRound,
    DateTimeOffset? CompletedAtBeforeRound,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string? CreationRequestKey,
    string? CreationRequestDigest,
    int Version);

/// <summary>One immutable submitted round.</summary>
public sealed record V2CountResultRecord(
    long Id,
    long CountTaskId,
    int Round,
    long BinId,
    string BinCode,
    long ProductId,
    string ProductName,
    string Sku,
    string Barcode,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    DateTimeOffset RoundStartedAt,
    long BookQuantityMicros,
    long ActualQuantityMicros,
    long DifferenceMicros,
    long SubmittedById,
    string SubmittedByName,
    string SubmittedByRole,
    DateTimeOffset SubmittedAt,
    string? Note,
    long? AdjustmentTransactionId);

/// <summary>
/// Immutable evidence of a confirm or resolve, read from the task's successful activity log entries (B4b):
/// confirm names its result, outcome, created adjustment and request key/digest; resolve lists every result it covered.
/// </summary>
public sealed record V2CountEvidence(
    long LogId,
    string Type,
    long? ActorId,
    long? ResultId,
    string? Outcome,
    long? TransactionId,
    IReadOnlyList<long> ResolvedResultIds,
    string? RequestKey,
    string? RequestDigest);

/// <summary>A task with its results ordered by round and its confirm/resolve evidence ordered by log ID.</summary>
public sealed record V2CountTaskDetail(
    V2CountTaskRecord Task, IReadOnlyList<V2CountResultRecord> Results, IReadOnlyList<V2CountEvidence>? Evidence = null);

/// <summary>A new count discrepancy alert (one open per task; trigger snapshot as JSON).</summary>
public sealed record V2NewDiscrepancyAlert(
    long CountTaskId, long CountResultId, long BinId, long ProductId, string Message, string TriggerSnapshotJson, DateTimeOffset Now);

/// <summary>A list row: the task and its latest result ID.</summary>
public sealed record V2CountTaskListRow(V2CountTaskRecord Task, long? LatestResultId);

/// <summary>Exact SQL filters of the count list; newest first (ID descending), IDs below <see cref="BeforeId"/>.</summary>
public sealed record V2CountTaskFilter(string? Status, long? BinId, bool? Unresolved, long? BeforeId);

/// <summary>Read side of counts (outside write transactions).</summary>
public interface IV2CountReader
{
    IAsyncEnumerable<V2CountTaskListRow> StreamCountTasksAsync(V2CountTaskFilter filter, CancellationToken cancellationToken);

    Task<V2CountTaskDetail?> FindCountTaskAsync(long id, CancellationToken cancellationToken);
}

/// <summary>The new state of a task after a command; creation fields never change.</summary>
public sealed record V2CountTaskUpdate(
    string Status,
    DateTimeOffset? CompletedAt,
    int CurrentRound,
    V2CountRoundSnapshot? Round,
    string? StatusBeforeRound,
    DateTimeOffset? CompletedAtBeforeRound,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool? HasUnresolvedInvestigation = null);

public sealed record V2NewCountTask(
    string Number,
    long BinId,
    string BinCode,
    long CreatorId,
    string CreatorName,
    string CreatorRole,
    V2CountRoundSnapshot Round,
    string CreationRequestKey,
    string CreationRequestDigest);

/// <summary>Count writes inside the shared write transaction.</summary>
public interface IV2CountWriteOperations
{
    Task<V2CountTaskDetail?> FindCountTaskDetailAsync(long id, CancellationToken cancellationToken);

    Task<V2CountTaskRecord?> FindCountTaskByCreationRequestAsync(long creatorId, string requestKey, CancellationToken cancellationToken);

    /// <summary>The bin's task that is in progress, pending confirmation or has an unresolved investigation, if any.</summary>
    Task<V2CountTaskRecord?> FindBlockingCountTaskAsync(long binId, CancellationToken cancellationToken);

    Task<long> InsertCountTaskAsync(V2NewCountTask task, CancellationToken cancellationToken);

    Task<long> InsertCountResultAsync(V2CountResultRecord result, CancellationToken cancellationToken);

    /// <summary>False when the stored version differs.</summary>
    Task<bool> UpdateCountTaskAsync(long id, int expectedVersion, V2CountTaskUpdate update, CancellationToken cancellationToken);

    /// <summary>Sets or clears <c>Bins.FrozenCountTaskId</c> (bumps the bin version).</summary>
    Task SetBinFrozenAsync(long binId, long? countTaskId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The successful confirm by this actor with this request key, if any (task and stored digest).</summary>
    Task<V2CountEvidence?> FindCountConfirmByRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a successful count log entry now (same transaction) with its task/result/transaction references set,
    /// and returns its ID.
    /// </summary>
    Task<long> AddCountEvidenceAsync(
        V2ActivityEntry entry, long countTaskId, long? countResultId, long? transactionId, CancellationToken cancellationToken);

    Task<long?> FindOpenDiscrepancyAlertAsync(long countTaskId, CancellationToken cancellationToken);

    Task<long> InsertDiscrepancyAlertAsync(V2NewDiscrepancyAlert alert, CancellationToken cancellationToken);
}
