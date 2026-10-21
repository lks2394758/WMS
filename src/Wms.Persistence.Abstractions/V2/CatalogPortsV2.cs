namespace Wms.Persistence.Abstractions.V2;

/// <summary>A unit with the number of products (archived included) that reference it.</summary>
public sealed record V2UnitRecord(long Id, string Name, bool IsSystem, int Version, int ProductCount);

/// <summary>A category with its direct product links (archived products included) and direct children.</summary>
public sealed record V2CategoryRecord(long Id, string Name, long? ParentId, int Version, int ProductCount, int ChildCount);

/// <summary>A product without its image bytes.</summary>
public sealed record V2ProductRecord(
    long Id,
    string Name,
    string Sku,
    string Barcode,
    IReadOnlyList<string> HistoricalSkus,
    IReadOnlyList<string> HistoricalBarcodes,
    long UnitId,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<long> CategoryIds,
    string? ImageContentType,
    int ImageLength,
    bool IsArchived,
    long CreatedById,
    string? CreationRequestKey,
    string? CreationRequestDigest,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public bool HasImage => ImageContentType is not null;
}

public sealed record V2ProductImage(byte[] Content, string ContentType);

/// <summary>One non-archived bin that references a product (current or retained), in micro-units.</summary>
public sealed record V2BinStockRecord(
    long BinId,
    string Code,
    long QuantityMicros,
    long? CapacityMicros,
    long? LowThresholdMicros,
    long? HighThresholdMicros,
    long? FrozenCountTaskId);

/// <summary>Warehouse default thresholds (micro-percent).</summary>
public sealed record V2ThresholdDefaults(long LowMicros, long HighMicros);

/// <summary>A product row of a list page with the quantities of its non-archived bins (summed exactly by the caller).</summary>
public sealed record V2ProductListRow(V2ProductRecord Product, IReadOnlyList<long> BinQuantities);

/// <summary>A product with image, stock distribution and whether an unfinished count or unresolved investigation involves it.</summary>
public sealed record V2ProductDetail(
    V2ProductRecord Product,
    V2ProductImage? Image,
    IReadOnlyList<V2BinStockRecord> Bins,
    bool HasOpenCountOrInvestigation);

/// <summary>
/// Product list filter. <see cref="Search"/> (trimmed) matches the current name, SKU or barcode as a substring,
/// case-insensitive by Unicode invariant upper case for all three (not SQLite's ASCII-only folding), applied before the
/// page is cut; <see cref="CategoryIds"/> (descendants already
/// expanded; empty = nothing matches) keeps products linked to any of them. Archived products are never listed. Keyset
/// paging by ID; at most <see cref="Limit"/> rows.
/// </summary>
public sealed record V2ProductFilter(string? Search, IReadOnlyCollection<long>? CategoryIds, long? AfterId, int Limit);

/// <summary>Read side of the v2 catalog (short-lived, outside write transactions).</summary>
public interface IV2CatalogReader
{
    Task<IReadOnlyList<V2UnitRecord>> ListUnitsAsync(string? normalizedSearch, long? afterId, int limit, CancellationToken cancellationToken);

    Task<V2UnitRecord?> FindUnitAsync(long id, CancellationToken cancellationToken);

    /// <summary>Every category (the tree is small; paths are computed by the caller).</summary>
    Task<IReadOnlyList<V2CategoryRecord>> ListCategoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<V2ProductListRow>> ListProductsAsync(V2ProductFilter filter, CancellationToken cancellationToken);

    /// <summary>The product (archived included; the caller decides) with image, bins and open-task state, or null.</summary>
    Task<V2ProductDetail?> FindProductAsync(long id, CancellationToken cancellationToken);

    Task<V2ThresholdDefaults> GetThresholdDefaultsAsync(CancellationToken cancellationToken);
}

/// <summary>Technical number counters kept in SystemSettings.</summary>
public enum V2Counter
{
    Sku,
    Barcode,
    SerialNumber,
    Transaction,
    CountTask,
}

/// <summary>Current and historical codes of one product (archived included): every one of them stays reserved.</summary>
public sealed record V2CodeOwner(
    long ProductId,
    string Sku,
    string Barcode,
    IReadOnlyList<string> HistoricalSkus,
    IReadOnlyList<string> HistoricalBarcodes);

public sealed record V2NewProduct(
    string Name,
    string Sku,
    string NormalizedSku,
    string Barcode,
    string NormalizedBarcode,
    long UnitId,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<long> CategoryIds,
    V2ProductImage? Image,
    long CreatedById,
    string? CreationRequestKey,
    string? CreationRequestDigest,
    DateTimeOffset Now);

/// <summary>
/// The full new state of an existing product. <see cref="ReplaceImage"/> false keeps the stored image; true stores
/// <see cref="Image"/> (null removes it). <see cref="Archive"/> sets the archive flag. Creation metadata never changes.
/// </summary>
public sealed record V2ProductUpdate(
    long Id,
    int ExpectedVersion,
    string Name,
    string Sku,
    string NormalizedSku,
    string Barcode,
    string NormalizedBarcode,
    IReadOnlyList<string> HistoricalSkus,
    IReadOnlyList<string> HistoricalBarcodes,
    long UnitId,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<long> CategoryIds,
    bool ReplaceImage,
    V2ProductImage? Image,
    bool Archive,
    DateTimeOffset Now);

/// <summary>Opens the single v2 write transaction for catalog changes (the same writer queue as identity writes).</summary>
public interface IV2CatalogStore
{
    Task<IV2CatalogWriteScope> BeginWriteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One catalog write in one <c>BEGIN IMMEDIATE</c> transaction holding the v2 writer lock from the first read: the
/// session re-check, every validation read, all changes and the activity log commit together, or nothing does
/// (dispose without <see cref="CommitAsync"/> rolls back). Update methods return false when the stored
/// <c>Version</c> differs from the expected one.
/// </summary>
public interface IV2CatalogWriteScope
    : IV2AdminWriteOperations, IV2InventoryWriteOperations, IV2SerialWriteOperations, IV2CountWriteOperations, IAsyncDisposable
{
    Task<V2AuthenticatedSession?> FindSessionAsync(long sessionId, CancellationToken cancellationToken);

    Task<V2UnitRecord?> FindUnitAsync(long id, CancellationToken cancellationToken);

    Task<V2UnitRecord?> FindUnitByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken);

    Task<long> InsertUnitAsync(string name, string normalizedName, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> UpdateUnitAsync(long id, int expectedVersion, string name, string normalizedName, DateTimeOffset now, CancellationToken cancellationToken);

    Task DeleteUnitAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<V2CategoryRecord>> ListCategoriesAsync(CancellationToken cancellationToken);

    Task<long> InsertCategoryAsync(string name, string normalizedName, long? parentId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> UpdateCategoryAsync(
        long id, int expectedVersion, string name, string normalizedName, long? parentId, DateTimeOffset now, CancellationToken cancellationToken);

    Task DeleteCategoryAsync(long id, CancellationToken cancellationToken);

    Task<V2ProductDetail?> FindProductAsync(long id, CancellationToken cancellationToken);

    Task<V2ProductRecord?> FindProductByCreationRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<V2CodeOwner>> ListProductCodesAsync(CancellationToken cancellationToken);

    /// <summary>Increments and returns the counter (never reused; gaps allowed if the transaction rolls back).</summary>
    Task<long> NextNumberAsync(V2Counter counter, CancellationToken cancellationToken);

    Task<long> InsertProductAsync(V2NewProduct product, CancellationToken cancellationToken);

    Task<bool> UpdateProductAsync(V2ProductUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// For the product's bins holding zero stock: clears capacity and threshold overrides, and with
    /// <paramref name="dropProduct"/> also the product reference (bin fully free). Returns the affected bin IDs.
    /// </summary>
    Task<IReadOnlyList<long>> ClearZeroStockBinsAsync(long productId, bool dropProduct, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Resolves the product's open capacity alerts (low, high, over capacity); returns their IDs. History is kept.</summary>
    Task<IReadOnlyList<long>> ResolveCapacityAlertsAsync(long productId, DateTimeOffset now, string note, CancellationToken cancellationToken);

    void AddActivity(V2ActivityEntry entry);

    Task CommitAsync(CancellationToken cancellationToken);
}
