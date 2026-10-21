namespace Wms.Contracts.V2.Bins;

/// <summary>
/// <c>GET /api/v2/bins</c> row and <c>GET /api/v2/bins/{id}</c>. Product fields describe the current or retained
/// product (a zero-quantity bin may keep its last product and capacity). <see cref="LowThreshold"/>/<see cref="HighThreshold"/>
/// are the bin's own overrides; the effective values combine them with the warehouse defaults.
/// </summary>
public sealed record BinResponse(
    string Id,
    string Code,
    string Zone,
    string Rack,
    string Level,
    string Cell,
    string Quantity,
    string CapacityStatus,
    string EffectiveLowThreshold,
    string EffectiveHighThreshold,
    bool HasUnresolvedInvestigation,
    IReadOnlyList<string> HistoricalCodes,
    int Version,
    string? ProductId = null,
    string? ProductName = null,
    string? Sku = null,
    string? UnitName = null,
    int? DecimalPlaces = null,
    bool? SerialTracking = null,
    string? Capacity = null,
    string? OccupancyPercent = null,
    string? LowThreshold = null,
    string? HighThreshold = null,
    string? FrozenCountTaskId = null);

/// <summary><c>POST /api/v2/bins</c> (no version) and <c>PUT /api/v2/bins/{id}</c> (version required; bin must be empty).</summary>
public sealed record BinWriteRequest(string Zone, string Rack, string Level, string Cell, int? Version = null);

/// <summary><c>POST /api/v2/bins/racks</c>. Omitted sizes use the warehouse defaults at creation time.</summary>
public sealed record RackCreateRequest(string Zone, string Rack, int? Levels = null, int? CellsPerLevel = null);

public sealed record RackCreateResponse(
    string Zone,
    string Rack,
    int Levels,
    int CellsPerLevel,
    int CreatedCount,
    string FirstCode,
    string LastCode);

/// <summary>One capacity state per bin, priority overCapacity &gt; high &gt; low &gt; normal.</summary>
public static class CapacityStatuses
{
    public const string NotConfigured = "notConfigured";
    public const string Normal = "normal";
    public const string Low = "low";
    public const string High = "high";
    public const string OverCapacity = "overCapacity";

    public static IReadOnlyList<string> All { get; } = [NotConfigured, Normal, Low, High, OverCapacity];
}
