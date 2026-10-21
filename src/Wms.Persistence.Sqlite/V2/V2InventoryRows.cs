namespace Wms.Persistence.Sqlite.V2;

// EF Core rows added in B3a (inventory). The ledger tables are append-only (database triggers); serial items are
// updated in place (position, stock flag, version) and never deleted.

internal sealed class V2TransactionRow
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public string Type { get; set; } = "";
    public long ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string ActorRole { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public string? Reason { get; set; }
    public long? CountResultId { get; set; }
    public string RequestKey { get; set; } = "";
    public string RequestDigest { get; set; } = "";
}

internal sealed class V2TransactionLineRow
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public int LineNo { get; set; }
    public long ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string UnitName { get; set; } = "";
    public int DecimalPlaces { get; set; }
    public long? FromBinId { get; set; }
    public string? FromBinCode { get; set; }
    public long? ToBinId { get; set; }
    public string? ToBinCode { get; set; }
    public long QuantityMicros { get; set; }
    public long? FromBeforeMicros { get; set; }
    public long? FromAfterMicros { get; set; }
    public long? ToBeforeMicros { get; set; }
    public long? ToAfterMicros { get; set; }
    public string? PackagingUnit { get; set; }
    public long? PackagingQuantityMicros { get; set; }
    public long? ConversionFactorMicros { get; set; }
}

internal sealed class V2TransactionLineItemRow
{
    public long Id { get; set; }
    public long TransactionLineId { get; set; }
    public long SerializedItemId { get; set; }
    public string SerialNumber { get; set; } = "";
}

internal sealed class V2SerializedItemRow
{
    public long Id { get; set; }
    public string SerialNumber { get; set; } = "";
    public string NormalizedSerialNumber { get; set; } = "";
    public string HistoricalSerialNumbers { get; set; } = "[]";
    public long ProductId { get; set; }
    public long? BinId { get; set; }
    public bool InStock { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; }
}
