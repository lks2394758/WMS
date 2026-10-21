namespace Wms.Persistence.Sqlite.V2;

// EF Core rows added in B2a (catalog). Categories, Products, ProductCategories, Bins and Alerts map every column;
// CountTasks and CountResults map every column since B4a.

internal sealed class V2CategoryRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public long? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class V2ProductRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string NormalizedSku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string NormalizedBarcode { get; set; } = "";
    public string HistoricalSkus { get; set; } = "[]";
    public string HistoricalBarcodes { get; set; } = "[]";
    public long UnitId { get; set; }
    public int DecimalPlaces { get; set; }
    public bool SerialTracking { get; set; }
    public byte[]? Image { get; set; }
    public string? ImageContentType { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public long CreatedById { get; set; }
    public string? CreationRequestKey { get; set; }
    public string? CreationRequestDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class V2ProductCategoryRow
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public long CategoryId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class V2BinRow
{
    public long Id { get; set; }
    public string Zone { get; set; } = "";
    public string Rack { get; set; } = "";
    public string Level { get; set; } = "";
    public string Cell { get; set; } = "";
    public string Code { get; set; } = "";
    public string NormalizedCode { get; set; } = "";
    public string HistoricalCodes { get; set; } = "[]";
    public long? ProductId { get; set; }
    public long QuantityMicros { get; set; }
    public long? CapacityMicros { get; set; }
    public long? LowThresholdMicros { get; set; }
    public long? HighThresholdMicros { get; set; }
    public long? FrozenCountTaskId { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}

internal sealed class V2CountTaskRow
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public long BinId { get; set; }
    public string BinCode { get; set; } = "";
    public long CreatorId { get; set; }
    public string CreatorName { get; set; } = "";
    public string CreatorRole { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? CompletedAt { get; set; }
    public int CurrentRound { get; set; }
    public bool HasUnresolvedInvestigation { get; set; }
    public DateTimeOffset? RoundStartedAt { get; set; }
    public long? RoundStartedById { get; set; }
    public string? RoundStartedByName { get; set; }
    public string? RoundStartedByRole { get; set; }
    public long? RoundProductId { get; set; }
    public string? RoundProductName { get; set; }
    public string? RoundSku { get; set; }
    public string? RoundBarcode { get; set; }
    public string? RoundUnitName { get; set; }
    public int? RoundDecimalPlaces { get; set; }
    public bool? RoundSerialTracking { get; set; }
    public long? RoundBookQuantityMicros { get; set; }
    public string? StatusBeforeRound { get; set; }
    public DateTimeOffset? CompletedAtBeforeRound { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public string? CreationRequestKey { get; set; }
    public string? CreationRequestDigest { get; set; }
    public int Version { get; set; }
}

internal sealed class V2CountResultRow
{
    public long Id { get; set; }
    public long CountTaskId { get; set; }
    public int Round { get; set; }
    public long BinId { get; set; }
    public string BinCode { get; set; } = "";
    public long ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string UnitName { get; set; } = "";
    public int DecimalPlaces { get; set; }
    public bool SerialTracking { get; set; }
    public DateTimeOffset RoundStartedAt { get; set; }
    public long BookQuantityMicros { get; set; }
    public long ActualQuantityMicros { get; set; }
    public long DifferenceMicros { get; set; }
    public long SubmittedById { get; set; }
    public string SubmittedByName { get; set; } = "";
    public string SubmittedByRole { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string? Note { get; set; }
}

internal sealed class V2AlertRow
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public long BinId { get; set; }
    public long ProductId { get; set; }
    public long? CountTaskId { get; set; }
    public long? CountResultId { get; set; }
    public string Message { get; set; } = "";
    public string TriggerSnapshot { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
    public int Version { get; set; }
}
