using Microsoft.AspNetCore.Mvc;
using Wms.Application.Catalog;
using Wms.Contracts.Catalog;
using Wms.Contracts.Common;

namespace Wms.Api.Controllers;

[Route("api/v1/products")]
public sealed class ProductsController(CatalogQueries catalog) : WmsControllerBase
{
    /// <summary>Products ordered by SKU, including disabled ones.</summary>
    [HttpGet]
    [ProducesResponseType<CursorPage<ProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<CursorPage<ProductResponse>>> List(
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        FromResult(await catalog.ListProductsAsync(cursor, limit, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<ProductResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await catalog.GetProductAsync(id, cancellationToken));
}

[Route("api/v1/locations")]
public sealed class LocationsController(CatalogQueries catalog) : WmsControllerBase
{
    /// <summary>Locations ordered by code, including disabled ones.</summary>
    [HttpGet]
    [ProducesResponseType<CursorPage<LocationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<CursorPage<LocationResponse>>> List(
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        FromResult(await catalog.ListLocationsAsync(cursor, limit, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<LocationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<LocationResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await catalog.GetLocationAsync(id, cancellationToken));
}

[Route("api/v1/barcodes")]
public sealed class BarcodesController(CatalogQueries catalog) : WmsControllerBase
{
    /// <summary>
    /// Resolves a scanned code to its product. The code is a query value (not a path segment) because barcodes may
    /// contain characters such as <c>/</c>.
    /// </summary>
    [HttpGet("resolve")]
    [ProducesResponseType<BarcodeResolutionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    public async Task<ActionResult<BarcodeResolutionResponse>> Resolve(
        [FromQuery] string? value, CancellationToken cancellationToken) =>
        FromResult(await catalog.ResolveBarcodeAsync(value, cancellationToken));
}
