namespace Wms.Persistence.Abstractions.V2;

/// <summary>A physical serial item: current number, corrected (historical) numbers, product, bin when in stock.</summary>
public sealed record V2SerializedItemRecord(
    long Id,
    string SerialNumber,
    IReadOnlyList<string> HistoricalSerialNumbers,
    long ProductId,
    long? BinId,
    bool InStock,
    int Version);

public sealed record V2NewSerializedItem(string SerialNumber, string NormalizedSerialNumber, long ProductId, long BinId);

/// <summary>New position of an existing item: in stock in <see cref="BinId"/>, or out of stock when null.</summary>
public sealed record V2SerializedItemMove(long Id, int ExpectedVersion, long? BinId);

/// <summary>New stock state of a bin changed by an inventory transaction (product, quantity, capacity, overrides).</summary>
public sealed record V2BinStockUpdate(
    long Id,
    int ExpectedVersion,
    long? ProductId,
    long QuantityMicros,
    long? CapacityMicros,
    long? LowThresholdMicros,
    long? HighThresholdMicros);

/// <summary>A submitted count result and its task's bin, for linking an adjustment to it.</summary>
public sealed record V2CountResultLink(long ResultId, long CountTaskId, long BinId, long ProductId);

public sealed record V2LineItem(long SerializedItemId, string SerialNumber);

/// <summary>An immutable ledger line: product and bin snapshots, signed quantity, before/after of each side, packaging, items.</summary>
public sealed record V2TransactionLineRecord(
    int LineNo,
    long ProductId,
    string ProductName,
    string Sku,
    string Barcode,
    string UnitName,
    int DecimalPlaces,
    long QuantityMicros,
    long? FromBinId,
    string? FromBinCode,
    long? FromBeforeMicros,
    long? FromAfterMicros,
    long? ToBinId,
    string? ToBinCode,
    long? ToBeforeMicros,
    long? ToAfterMicros,
    string? PackagingUnit,
    long? PackagingQuantityMicros,
    long? ConversionFactorMicros,
    IReadOnlyList<V2LineItem> Items);

/// <summary>A new successful transaction with its request key and canonical body digest (idempotency, decisions §9).</summary>
public sealed record V2NewTransaction(
    string Number,
    string Type,
    long ActorId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    string? Source,
    string? Destination,
    string? Reason,
    long? CountResultId,
    string RequestKey,
    string RequestDigest,
    IReadOnlyList<V2TransactionLineRecord> Lines);

/// <summary>A committed transaction as stored (immutable).</summary>
public sealed record V2TransactionRecord(
    long Id,
    string Number,
    string Type,
    long ActorId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    string? Source,
    string? Destination,
    string? Reason,
    long? CountResultId,
    long? CountTaskId,
    string RequestDigest,
    IReadOnlyList<V2TransactionLineRecord> Lines);

/// <summary>A transaction header for lists, with the product names and SKUs of its lines (for searching).</summary>
public sealed record V2TransactionSummary(
    long Id,
    string Number,
    string Type,
    long ActorId,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt,
    int LineCount,
    string? Source,
    string? Destination,
    string? Reason,
    long? CountResultId,
    IReadOnlyList<string> ProductTexts);

/// <summary>
/// Transaction list filter applied in SQL; text search is applied by the caller. Newest first (descending ID);
/// <see cref="BeforeId"/> continues after the previous batch.
/// </summary>
public sealed record V2TransactionFilter(
    string? Type,
    DateTimeOffset? From,
    DateTimeOffset? To,
    long? ProductId,
    long? BinId,
    long? ActorId,
    long? BeforeId,
    int BatchSize);

/// <summary>Inventory changes inside the single v2 write transaction (see <see cref="IV2CatalogWriteScope"/>).</summary>
public interface IV2InventoryWriteOperations
{
    /// <summary>Products by ID (archived included), without image bytes.</summary>
    Task<IReadOnlyDictionary<long, V2ProductRecord>> FindProductRecordsAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    Task<(long Id, string RequestDigest)?> FindTransactionByRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken);

    Task<V2TransactionRecord?> FindTransactionAsync(long id, CancellationToken cancellationToken);

    Task<V2CountResultLink?> FindCountResultAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Items whose current number is one of <paramref name="normalizedNumbers"/>, plus every item that has corrected
    /// (historical) numbers, so the caller can match old numbers with Unicode-correct normalization.
    /// </summary>
    Task<IReadOnlyList<V2SerializedItemRecord>> FindSerializedItemCandidatesAsync(
        IReadOnlyCollection<string> normalizedNumbers, CancellationToken cancellationToken);

    /// <summary>Every current and historical serial number (for generating numbers that were never used).</summary>
    Task<IReadOnlyList<V2SerializedItemRecord>> ListSerialNumbersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<long>> InsertSerializedItemsAsync(IReadOnlyList<V2NewSerializedItem> items, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Moves existing items; false if any version changed underneath.</summary>
    Task<bool> MoveSerializedItemsAsync(IReadOnlyList<V2SerializedItemMove> moves, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> UpdateBinStockAsync(V2BinStockUpdate update, DateTimeOffset now, CancellationToken cancellationToken);

    Task<long> InsertTransactionAsync(V2NewTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>An item with its product's current name/SKU (archived products included) and its bin's current code.</summary>
public sealed record V2SerializedItemView(V2SerializedItemRecord Item, string ProductName, string Sku, string? BinCode);

/// <summary>One ledger line that touched the item, from the immutable line and item-link snapshots.</summary>
public sealed record V2SerialHistoryRecord(
    long TransactionId,
    string TransactionNumber,
    string Type,
    DateTimeOffset OccurredAt,
    string ActorName,
    string ActorRole,
    string SerialNumber,
    string? FromBinCode,
    string? ToBinCode);

/// <summary>One correction, read from its immutable activity log entry.</summary>
public sealed record V2SerialCorrectionRecord(
    long ActivityLogId,
    string PreviousSerialNumber,
    string NewSerialNumber,
    string Reason,
    string ActorName,
    string ActorRole,
    DateTimeOffset OccurredAt);

public sealed record V2SerializedItemDetail(
    V2SerializedItemView View, IReadOnlyList<V2SerialHistoryRecord> History, IReadOnlyList<V2SerialCorrectionRecord> Corrections);

/// <summary>Read side of serialized items (B3b).</summary>
public interface IV2SerialReader
{
    /// <summary>Items by ascending ID after <paramref name="afterId"/>, with exact filters applied in SQL; streamed.</summary>
    IAsyncEnumerable<V2SerializedItemView> StreamSerializedItemsAsync(
        long? productId, long? binId, bool? inStock, long? afterId, CancellationToken cancellationToken);

    Task<V2SerializedItemDetail?> FindSerializedItemAsync(long id, CancellationToken cancellationToken);
}

/// <summary>Serial number corrections inside the single v2 write transaction (see <see cref="IV2CatalogWriteScope"/>).</summary>
public interface IV2SerialWriteOperations
{
    Task<V2SerializedItemRecord?> FindSerializedItemAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the current number and the full historical list of one item if its version is unchanged; bumps the version.
    /// Product, bin and stock flag are never changed here.
    /// </summary>
    Task<bool> CorrectSerialNumberAsync(
        long id, int expectedVersion, string serialNumber, string normalizedSerialNumber, IReadOnlyList<string> historicalSerialNumbers,
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Read side of the immutable ledger.</summary>
public interface IV2InventoryReader
{
    Task<IReadOnlyList<V2TransactionSummary>> ListTransactionsAsync(V2TransactionFilter filter, CancellationToken cancellationToken);

    Task<V2TransactionRecord?> FindTransactionAsync(long id, CancellationToken cancellationToken);
}
