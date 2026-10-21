namespace Wms.Persistence.Sqlite.V2;

// EF Core rows of the v2 tables used so far (B1: identity, settings, units, activity log). The migration creates all
// 16 business tables with their complete columns; B2-B4 add rows for the others. Quantities and percentages are
// micro-units (value x 1 000 000) in 64-bit INTEGER columns; JSON arrays are TEXT.

internal sealed class V2UserRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Roles { get; set; } = "[]";
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class V2LoginSessionRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string CurrentRole { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset LoggedInAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LoggedOutAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public int Version { get; set; }
}

internal sealed class V2SystemSettingsRow
{
    public const long SingletonId = 1;

    public long Id { get; set; }
    public int DefaultRackLevels { get; set; }
    public int DefaultCellsPerLevel { get; set; }
    public long LowThresholdMicros { get; set; }
    public long HighThresholdMicros { get; set; }
    public int SessionHours { get; set; }
    public long LastSkuNumber { get; set; }
    public long LastBarcodeNumber { get; set; }
    public long LastSerialNumber { get; set; }
    public long LastTransactionNumber { get; set; }
    public long LastCountTaskNumber { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long? UpdatedById { get; set; }
    public int Version { get; set; }
}

internal sealed class V2UnitRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class V2ActivityLogRow
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? ActorRole { get; set; }
    public string? LoginName { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Success { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public string Subjects { get; set; } = "[]";
    public string? Changes { get; set; }
    public long? TransactionId { get; set; }
    public long? CountTaskId { get; set; }
    public long? CountResultId { get; set; }
}
