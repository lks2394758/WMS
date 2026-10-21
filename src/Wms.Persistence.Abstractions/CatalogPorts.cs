using Wms.Domain.Catalog;
using Wms.Domain.Common;

namespace Wms.Persistence.Abstractions;

/// <summary>Read-only catalog queries. Returned entities are detached copies; changing them changes nothing stored.</summary>
public interface ICatalogReader
{
    Task<Product?> FindProductAsync(ProductId id, CancellationToken cancellationToken);

    /// <summary>Products ordered by SKU (the page key).</summary>
    Task<PageResult<Product>> ListProductsAsync(PageRequest page, CancellationToken cancellationToken);

    Task<Location?> FindLocationAsync(LocationId id, CancellationToken cancellationToken);

    /// <summary>Locations ordered by code (the page key).</summary>
    Task<PageResult<Location>> ListLocationsAsync(PageRequest page, CancellationToken cancellationToken);

    /// <summary>Active barcode values per product, ordered. Products without active barcodes are absent.</summary>
    Task<IReadOnlyDictionary<ProductId, IReadOnlyList<string>>> GetActiveBarcodesAsync(
        IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken);

    /// <summary>Every active barcode with exactly this value. More than one product means the barcode is ambiguous.</summary>
    Task<IReadOnlyList<Barcode>> FindActiveBarcodesAsync(string value, CancellationToken cancellationToken);
}
