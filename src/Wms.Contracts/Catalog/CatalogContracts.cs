namespace Wms.Contracts.Catalog;

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string Unit,
    int? Capacity,
    bool IsActive,
    IReadOnlyList<string> Barcodes);

public sealed record LocationResponse(Guid Id, Guid WarehouseId, string Code, bool IsActive);

public sealed record BarcodeResolutionResponse(string Barcode, ProductResponse Product);
