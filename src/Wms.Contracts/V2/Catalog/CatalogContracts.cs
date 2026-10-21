namespace Wms.Contracts.V2.Catalog;

/// <summary><c>GET /api/v2/units</c> row. <see cref="IsSystem"/> marks the preset unit "件", which cannot be deleted.</summary>
public sealed record UnitResponse(string Id, string Name, bool IsSystem, int ProductCount, int Version);

/// <summary><c>POST /api/v2/units</c> (no version) and <c>PUT /api/v2/units/{id}</c> (version required).</summary>
public sealed record UnitWriteRequest(string Name, int? Version = null);

/// <summary>
/// <c>GET /api/v2/categories</c> row. <see cref="Path"/> is the display path from the root, e.g. "工具 / 电动工具".
/// <see cref="ProductCount"/> counts direct links including archived products (the delete check).
/// </summary>
public sealed record CategoryResponse(
    string Id,
    string Name,
    string Path,
    int ProductCount,
    int ChildCount,
    int Version,
    string? ParentId = null);

/// <summary><c>POST /api/v2/categories</c> and <c>PUT /api/v2/categories/{id}</c>; no <see cref="ParentId"/> means top level.</summary>
public sealed record CategoryWriteRequest(string Name, string? ParentId = null, int? Version = null);

/// <summary><c>GET /api/v2/products</c> row. Never carries image bytes; <see cref="HasImage"/> tells whether one exists.</summary>
public sealed record ProductListItem(
    string Id,
    string Name,
    string Sku,
    string Barcode,
    string UnitId,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<string> CategoryIds,
    string TotalQuantity,
    bool HasImage,
    int Version);

/// <summary>One bin that currently references the product (zero-quantity bins that kept it included).</summary>
public sealed record ProductBinStock(
    string BinId,
    string BinCode,
    string Quantity,
    string CapacityStatus,
    string? Capacity = null,
    string? OccupancyPercent = null,
    string? FrozenCountTaskId = null);

/// <summary><c>GET /api/v2/products/{id}</c>, also returned by product create and update.</summary>
public sealed record ProductResponse(
    string Id,
    string Name,
    string Sku,
    string Barcode,
    string UnitId,
    string UnitName,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<string> CategoryIds,
    string TotalQuantity,
    bool HasImage,
    IReadOnlyList<string> HistoricalSkus,
    IReadOnlyList<string> HistoricalBarcodes,
    IReadOnlyList<ProductBinStock> Bins,
    bool HasOpenCountOrInvestigation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    string? ImageBase64 = null,
    string? ImageContentType = null);

/// <summary>
/// <c>POST /api/v2/products</c>. Empty <see cref="Sku"/>/<see cref="Barcode"/> generate <c>SKU-000001</c>/<c>BC-000001</c>.
/// <see cref="ImageBase64"/> and <see cref="ImageContentType"/> come together or not at all.
/// </summary>
public sealed record ProductCreateRequest(
    string Name,
    string UnitId,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<string> CategoryIds,
    string? Sku = null,
    string? Barcode = null,
    string? ImageBase64 = null,
    string? ImageContentType = null);

/// <summary>
/// <c>PUT /api/v2/products/{id}</c>: full replacement of the editable fields. Null <see cref="Sku"/>/<see cref="Barcode"/>
/// keep the current code. Image: omitted/false <see cref="RemoveImage"/> and no upload keeps it; an upload replaces it;
/// <see cref="RemoveImage"/> true removes it; true together with an upload is <c>400 VALIDATION_FAILED</c>.
/// </summary>
public sealed record ProductUpdateRequest(
    string Name,
    string UnitId,
    int DecimalPlaces,
    bool SerialTracking,
    IReadOnlyList<string> CategoryIds,
    int Version,
    string? Sku = null,
    string? Barcode = null,
    string? ImageBase64 = null,
    string? ImageContentType = null,
    bool? RemoveImage = null);

/// <summary>Accepted image formats (verified against the actual bytes, at most 2 MiB decoded).</summary>
public static class ImageContentTypes
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    public static IReadOnlyList<string> All { get; } = [Png, Jpeg];
}
