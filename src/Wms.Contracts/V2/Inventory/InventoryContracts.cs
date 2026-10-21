namespace Wms.Contracts.V2.Inventory;

/// <summary>
/// Body of <c>POST /api/v2/inventory/receipts|shipments|movements|adjustments</c> (header <c>Idempotency-Key</c> required).
/// The whole request commits or fails as one transaction. <see cref="Source"/> is for receipts, <see cref="Destination"/>
/// for shipments; <see cref="Reason"/> is required for adjustments. <see cref="CountResultId"/> (adjustments only)
/// links the adjustment to the count result it corrects.
/// </summary>
public sealed record InventoryOperationRequest(
    IReadOnlyList<InventoryLine> Lines,
    string? Source = null,
    string? Destination = null,
    string? Reason = null,
    string? CountResultId = null);

/// <summary>
/// One product in one bin. Which members apply depends on the operation (contracts/v2/README.md, "Inventory lines").
/// Receipts/adjustments use <see cref="ToBinId"/>; shipments use <see cref="FromBinId"/>; movements use both.
/// </summary>
public sealed record InventoryLine(
    string? ProductId = null,
    string? FromBinId = null,
    string? ToBinId = null,
    string? Quantity = null,
    string? Capacity = null,
    string? LowThreshold = null,
    string? HighThreshold = null,
    string? PackagingUnit = null,
    string? PackagingQuantity = null,
    string? ConversionFactor = null,
    IReadOnlyList<string>? SerialNumbers = null,
    int? GenerateSerialCount = null,
    InlineNewProduct? NewProduct = null);

/// <summary>
/// Receipt-only product creation (also allowed for <c>receivingClerk</c>). Exactly one of <see cref="UnitId"/> and
/// <see cref="NewUnitName"/>; a new unit name that already exists (case-insensitive) reuses that unit.
/// </summary>
public sealed record InlineNewProduct(
    string Name,
    int DecimalPlaces,
    bool SerialTracking,
    string? UnitId = null,
    string? NewUnitName = null,
    string? Sku = null,
    string? Barcode = null);

/// <summary>One physical item touched by a transaction line, with the serial number as it was at that time.</summary>
public sealed record LineSerialItem(string SerializedItemId, string SerialNumber);

/// <summary>Immutable ledger line with product and bin snapshots and before/after quantities of each side.</summary>
public sealed record TransactionLineDetail(
    int LineNo,
    string ProductId,
    string ProductName,
    string Sku,
    string Barcode,
    string UnitName,
    int DecimalPlaces,
    string Quantity,
    string? FromBinId = null,
    string? FromBinCode = null,
    string? FromBefore = null,
    string? FromAfter = null,
    string? ToBinId = null,
    string? ToBinCode = null,
    string? ToBefore = null,
    string? ToAfter = null,
    string? PackagingUnit = null,
    string? PackagingQuantity = null,
    string? ConversionFactor = null,
    IReadOnlyList<LineSerialItem>? Items = null);

/// <summary>Returned by every inventory write, by count confirmation adjustments and by <c>GET /api/v2/inventory/transactions/{id}</c>.</summary>
public sealed record InventoryTransactionResponse(
    string Id,
    string Number,
    string Type,
    string ActorId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    IReadOnlyList<TransactionLineDetail> Lines,
    string? Source = null,
    string? Destination = null,
    string? Reason = null,
    string? CountTaskId = null,
    string? CountResultId = null);

/// <summary><c>GET /api/v2/inventory/transactions</c> row.</summary>
public sealed record TransactionListItem(
    string Id,
    string Number,
    string Type,
    string ActorId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    int LineCount,
    string? Source = null,
    string? Destination = null,
    string? Reason = null,
    string? CountResultId = null);

/// <summary>v2 transaction types. A count correction is an <see cref="Adjustment"/> linked to a count result.</summary>
public static class TransactionTypesV2
{
    public const string Receipt = "receipt";
    public const string Shipment = "shipment";
    public const string Movement = "movement";
    public const string Adjustment = "adjustment";

    public static IReadOnlyList<string> All { get; } = [Receipt, Shipment, Movement, Adjustment];
}

/// <summary>
/// <c>GET /api/v2/serialized-items</c> row. <see cref="MatchedHistoricalSerialNumber"/> is set when the search matched a
/// corrected (old) number: show it as "corrected to <see cref="SerialNumber"/>"; the old number cannot be used in operations.
/// </summary>
public sealed record SerializedItemListItem(
    string Id,
    string SerialNumber,
    string ProductId,
    string ProductName,
    string Sku,
    bool InStock,
    IReadOnlyList<string> HistoricalSerialNumbers,
    int Version,
    string? BinId = null,
    string? BinCode = null,
    string? MatchedHistoricalSerialNumber = null);

/// <summary>One ledger line that touched the item; <see cref="SerialNumber"/> is the snapshot at that time.</summary>
public sealed record SerialHistoryEntry(
    string TransactionId,
    string TransactionNumber,
    string Type,
    DateTimeOffset OccurredAt,
    string ActorName,
    string ActorRole,
    string SerialNumber,
    string? FromBinCode = null,
    string? ToBinCode = null);

/// <summary>One serial number correction, read from the append-only activity log.</summary>
public sealed record SerialCorrectionEntry(
    string ActivityLogId,
    string PreviousSerialNumber,
    string NewSerialNumber,
    string Reason,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt);

/// <summary><c>GET /api/v2/serialized-items/{id}</c>, also returned by the correction.</summary>
public sealed record SerializedItemResponse(
    string Id,
    string SerialNumber,
    string ProductId,
    string ProductName,
    string Sku,
    bool InStock,
    IReadOnlyList<string> HistoricalSerialNumbers,
    IReadOnlyList<SerialHistoryEntry> History,
    IReadOnlyList<SerialCorrectionEntry> Corrections,
    int Version,
    string? BinId = null,
    string? BinCode = null);

/// <summary><c>POST /api/v2/serialized-items/{id}/correct</c> (permission <c>SerialNumbers.Correct</c>).</summary>
public sealed record SerialCorrectionRequest(string SerialNumber, string Reason, int Version);
