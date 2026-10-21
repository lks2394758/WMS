namespace Wms.Persistence.Abstractions.V2;

/// <summary>
/// A bin with its current or retained product (name, SKU, unit, precision), stock, capacity, threshold overrides,
/// frozen task and count state, in micro-units. <see cref="HasOpenCount"/>: a task of the bin is in progress or pending
/// confirmation; <see cref="HasUnresolvedInvestigation"/>: a task of the bin has an unresolved investigation;
/// <see cref="OpenCountTaskId"/>: that task (at most one per bin by a unique index), for problem subjects.
/// </summary>
public sealed record V2BinRecord(
    long Id,
    string Zone,
    string Rack,
    string Level,
    string Cell,
    string Code,
    IReadOnlyList<string> HistoricalCodes,
    long? ProductId,
    string? ProductName,
    string? Sku,
    string? UnitName,
    int? DecimalPlaces,
    bool? SerialTracking,
    long QuantityMicros,
    long? CapacityMicros,
    long? LowThresholdMicros,
    long? HighThresholdMicros,
    long? FrozenCountTaskId,
    bool IsArchived,
    bool HasOpenCount,
    bool HasUnresolvedInvestigation,
    int Version,
    long? OpenCountTaskId = null);

/// <summary>Current and historical codes of one bin (archived included): every one of them stays reserved.</summary>
public sealed record V2BinCodeOwner(long BinId, string Code, IReadOnlyList<string> HistoricalCodes);

public sealed record V2NewBin(string Zone, string Rack, string Level, string Cell, string Code, string NormalizedCode);

public sealed record V2BinPositionUpdate(
    long Id,
    int ExpectedVersion,
    string Zone,
    string Rack,
    string Level,
    string Cell,
    string Code,
    string NormalizedCode,
    IReadOnlyList<string> HistoricalCodes,
    DateTimeOffset Now);

/// <summary>The warehouse settings row (technical counters excluded).</summary>
public sealed record V2SettingsRecord(
    int DefaultRackLevels,
    int DefaultCellsPerLevel,
    long LowThresholdMicros,
    long HighThresholdMicros,
    int SessionHours,
    int Version,
    DateTimeOffset UpdatedAt,
    long? UpdatedById,
    string? UpdatedByName);

public sealed record V2SettingsUpdate(
    int ExpectedVersion,
    int DefaultRackLevels,
    int DefaultCellsPerLevel,
    long LowThresholdMicros,
    long HighThresholdMicros,
    int SessionHours,
    long UpdatedById,
    DateTimeOffset Now);

/// <summary>Everything needed to evaluate one non-archived bin's capacity alert, with its open capacity alert if any.</summary>
public sealed record V2CapacityState(
    long BinId,
    string Code,
    long? ProductId,
    string? Sku,
    string? UnitName,
    int? DecimalPlaces,
    long QuantityMicros,
    long? CapacityMicros,
    long? LowThresholdMicros,
    long? HighThresholdMicros,
    long? OpenAlertId,
    string? OpenAlertType);

/// <summary>A new open alert. <see cref="TriggerSnapshotJson"/> is a JSON object written once and never changed.</summary>
public sealed record V2NewAlert(string Type, long BinId, long ProductId, string Message, string TriggerSnapshotJson, DateTimeOffset Now);

/// <summary>An account for administration: never exposes the password hash outside the application layer.</summary>
public sealed record V2UserSummary(
    long Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    bool HasActiveSession,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

public sealed record V2NewUser(
    string Username,
    string NormalizedUsername,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    string PasswordHash,
    DateTimeOffset Now);

/// <summary>The new state of an account; <see cref="PasswordHash"/> null keeps the stored hash. Always bumps Version.</summary>
public sealed record V2UserUpdate(
    long Id,
    int ExpectedVersion,
    string Username,
    string NormalizedUsername,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    string? PasswordHash,
    DateTimeOffset Now);

/// <summary>Read side of bins, users and settings (short-lived, outside write transactions).</summary>
public interface IV2AdminReader
{
    /// <summary>Non-archived bins in code order (normalized code, then ID), optionally of one product; streamed.</summary>
    IAsyncEnumerable<V2BinRecord> StreamBinsAsync(long? productId, CancellationToken cancellationToken);

    /// <summary>The bin (archived included; the caller decides), or null.</summary>
    Task<V2BinRecord?> FindBinAsync(long id, CancellationToken cancellationToken);

    /// <summary>All accounts by ID; <see cref="V2UserSummary.HasActiveSession"/> evaluated at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<V2UserSummary>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<V2SettingsRecord> GetSettingsAsync(CancellationToken cancellationToken);
}

/// <summary>Bin, account and settings changes inside the single v2 write transaction (see <see cref="IV2CatalogWriteScope"/>).</summary>
public interface IV2AdminWriteOperations
{
    Task<V2BinRecord?> FindBinAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<V2BinCodeOwner>> ListBinCodesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the bins (one statement batch); returns their IDs in the given order.</summary>
    Task<IReadOnlyList<long>> InsertBinsAsync(IReadOnlyList<V2NewBin> bins, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> UpdateBinPositionAsync(V2BinPositionUpdate update, CancellationToken cancellationToken);

    Task<bool> ArchiveBinAsync(long id, int expectedVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Resolves the open capacity alerts (low, high, over capacity) of one bin; returns their IDs.</summary>
    Task<IReadOnlyList<long>> ResolveBinCapacityAlertsAsync(long binId, DateTimeOffset now, string note, CancellationToken cancellationToken);

    Task<V2SettingsRecord> GetSettingsAsync(CancellationToken cancellationToken);

    Task<bool> UpdateSettingsAsync(V2SettingsUpdate update, CancellationToken cancellationToken);

    /// <summary>Every non-archived bin with its open capacity alert (at most one per bin).</summary>
    Task<IReadOnlyList<V2CapacityState>> ListCapacityStatesAsync(CancellationToken cancellationToken);

    /// <summary>The same for the given non-archived bins only (as changed earlier in this transaction).</summary>
    Task<IReadOnlyList<V2CapacityState>> ListCapacityStatesAsync(IReadOnlyCollection<long> binIds, CancellationToken cancellationToken);

    /// <summary>Resolves one open alert (status, time, note only; the opening evidence is immutable).</summary>
    Task ResolveAlertAsync(long alertId, DateTimeOffset now, string note, CancellationToken cancellationToken);

    Task<long> InsertAlertAsync(V2NewAlert alert, CancellationToken cancellationToken);

    /// <summary>All accounts (with password hashes, for the application layer only).</summary>
    Task<IReadOnlyList<V2UserRecord>> ListUsersAsync(CancellationToken cancellationToken);

    Task<V2UserSummary?> FindUserSummaryAsync(long id, DateTimeOffset now, CancellationToken cancellationToken);

    Task<long> InsertUserAsync(V2NewUser user, CancellationToken cancellationToken);

    Task<bool> UpdateUserAsync(V2UserUpdate update, CancellationToken cancellationToken);

    /// <summary>Revokes every open session of the account (not logged out, not revoked); returns how many.</summary>
    Task<int> RevokeSessionsAsync(long userId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}
