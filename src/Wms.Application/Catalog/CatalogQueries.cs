using Wms.Application.Common;
using Wms.Contracts.Catalog;
using Wms.Contracts.Common;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Catalog;

/// <summary>Product, location and barcode reads. Inactive records are returned with <c>isActive: false</c>.</summary>
public sealed class CatalogQueries(ICatalogReader catalog)
{
    private static readonly IReadOnlyList<string> NoBarcodes = [];

    public async Task<Result<CursorPage<ProductResponse>>> ListProductsAsync(
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var page = Paging.Parse(cursor, limit);
        if (page.IsFailure)
        {
            return page.Error!;
        }

        var products = await catalog.ListProductsAsync(page.Value, cancellationToken);
        var barcodes = await catalog.GetActiveBarcodesAsync(products.Items.Select(p => p.Id).ToList(), cancellationToken);
        return Paging.ToPage(products, p => ContractMapper.ToResponse(p, barcodes.GetValueOrDefault(p.Id, NoBarcodes)));
    }

    public async Task<Result<ProductResponse>> GetProductAsync(Guid id, CancellationToken cancellationToken)
    {
        var productId = new ProductId(id);
        var product = await catalog.FindProductAsync(productId, cancellationToken);
        if (product is null)
        {
            return ApplicationErrors.ProductNotFound(id);
        }

        var barcodes = await catalog.GetActiveBarcodesAsync([productId], cancellationToken);
        return ContractMapper.ToResponse(product, barcodes.GetValueOrDefault(productId, NoBarcodes));
    }

    public async Task<Result<CursorPage<LocationResponse>>> ListLocationsAsync(
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var page = Paging.Parse(cursor, limit);
        if (page.IsFailure)
        {
            return page.Error!;
        }

        var locations = await catalog.ListLocationsAsync(page.Value, cancellationToken);
        return Paging.ToPage(locations, l => ContractMapper.ToResponse(l));
    }

    public async Task<Result<LocationResponse>> GetLocationAsync(Guid id, CancellationToken cancellationToken)
    {
        var location = await catalog.FindLocationAsync(new LocationId(id), cancellationToken);
        if (location is null)
        {
            return ApplicationErrors.LocationNotFound(id);
        }

        return ContractMapper.ToResponse(location);
    }

    /// <summary>Resolves a scanned code to exactly one product (guide §7.4).</summary>
    public async Task<Result<BarcodeResolutionResponse>> ResolveBarcodeAsync(string? value, CancellationToken cancellationToken)
    {
        var code = value?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return DomainErrors.Validation("value is required.", "value");
        }

        var matches = await catalog.FindActiveBarcodesAsync(code, cancellationToken);
        var productIds = matches.Select(b => b.ProductId).Distinct().ToList();
        if (productIds.Count > 1)
        {
            return ApplicationErrors.BarcodeAmbiguous(code);
        }

        var product = productIds.Count == 1 ? await catalog.FindProductAsync(productIds[0], cancellationToken) : null;
        if (product is null)
        {
            return ApplicationErrors.BarcodeNotFound(code);
        }

        var barcodes = await catalog.GetActiveBarcodesAsync([product.Id], cancellationToken);
        return new BarcodeResolutionResponse(code, ContractMapper.ToResponse(product, barcodes.GetValueOrDefault(product.Id, NoBarcodes)));
    }
}
