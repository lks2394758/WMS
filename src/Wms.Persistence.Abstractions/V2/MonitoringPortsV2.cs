namespace Wms.Persistence.Abstractions.V2;

/// <summary>
/// An alert as stored. <see cref="TriggerJson"/> is the immutable snapshot taken when it opened; bin code and product
/// name are the current labels of the referenced objects (the alert row stores IDs only).
/// </summary>
public sealed record V2AlertView(
    long Id,
    string Type,
    string Status,
    long BinId,
    string BinCode,
    long ProductId,
    string ProductName,
    string Message,
    DateTimeOffset CreatedAt,
    string TriggerJson,
    long? CountTaskId,
    long? CountResultId,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

/// <summary>A subject exactly as written in the log entry (type, ID text and label at that time).</summary>
public sealed record V2LoggedSubject(string Type, string Id, string? Label);

/// <summary>An immutable activity log entry with its actor, subjects and changes as written then.</summary>
public sealed record V2ActivityRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string Type,
    string Description,
    bool Success,
    IReadOnlyList<V2LoggedSubject> Subjects,
    long? ActorId,
    string? ActorName,
    string? ActorRole,
    string? LoginName,
    string? FailureCode,
    string? FailureReason,
    IReadOnlyList<V2FieldChange>? Changes);

/// <summary>Exact alert filters; newest first (ID descending) below <see cref="BeforeId"/>, at most <see cref="Limit"/>.</summary>
public sealed record V2AlertFilter(string? Status, string? Type, long? BinId, long? ProductId, long? BeforeId, int Limit);

/// <summary>
/// Exact activity filters (<see cref="From"/> inclusive, <see cref="To"/> exclusive; product/bin match a subject of that
/// type and ID); newest first below <see cref="BeforeId"/>, at most <see cref="Limit"/>.
/// </summary>
public sealed record V2ActivityFilter(
    DateTimeOffset? From, DateTimeOffset? To, long? ActorId, long? ProductId, long? BinId, string? Type, bool? Success, long? BeforeId, int Limit);

/// <summary>Dashboard figures read in one consistent snapshot.</summary>
public sealed record V2DashboardSnapshot(
    int SkuCount,
    int OpenAlertCount,
    int ActiveCountCount,
    int UnresolvedInvestigationCount,
    IReadOnlyList<V2ActivityRecord> RecentActivity,
    IReadOnlyList<V2AlertView> OpenAlerts);

/// <summary>Read side of alerts, the activity log and the dashboard (outside write transactions).</summary>
public interface IV2MonitoringReader
{
    Task<IReadOnlyList<V2AlertView>> ListAlertsAsync(V2AlertFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<V2ActivityRecord>> ListActivityAsync(V2ActivityFilter filter, CancellationToken cancellationToken);

    /// <summary>Counts, the latest <paramref name="take"/> log entries (of <paramref name="actorId"/> when set) and open alerts.</summary>
    Task<V2DashboardSnapshot> GetDashboardAsync(long? actorId, int take, CancellationToken cancellationToken);
}
