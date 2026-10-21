using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;

namespace Wms.Api.V2;

/// <summary>
/// Shared plumbing of the v2 controllers: the validated session of the request and result mapping. Controllers stay
/// thin: every rule lives in the Application services; reads need a signed-in user (fallback policy), writes also the
/// permission policy, re-checked by the service inside its write transaction.
/// </summary>
[ApiController]
internal abstract class V2ControllerBase : ControllerBase
{
    protected V2SessionPrincipal? Principal => V2SessionAuthenticationHandler.PrincipalOf(HttpContext);

    protected IActionResult Reply<T>(V2Result<T> result, int successStatus = StatusCodes.Status200OK)
    {
        if (!result.IsSuccess)
        {
            return V2Problems.Result(HttpContext, result.Failure!);
        }

        if (result.Replayed)
        {
            Response.Headers[ApiHeaders.IdempotentReplayed] = "true";
        }

        return result.Value is V2None ? NoContent() : StatusCode(successStatus, result.Value);
    }

    protected IActionResult Unauthenticated() => V2Problems.Result(HttpContext, V2Errors.Unauthenticated());
}

/// <summary><c>/api/v2/units</c> (contracts/v2/README.md §4).</summary>
[Route("api/v2/units")]
internal sealed class V2UnitsController(V2UnitService units) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Reply(await units.ListAsync(search, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await units.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Create([FromBody] UnitWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await units.CreateAsync(principal, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPut("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Update(string id, [FromBody] UnitWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await units.UpdateAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Delete(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteRequest? request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await units.DeleteAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}

/// <summary><c>/api/v2/categories</c> (contracts/v2/README.md §4).</summary>
[Route("api/v2/categories")]
internal sealed class V2CategoriesController(V2CategoryService categories) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search, [FromQuery] string? parentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Reply(await categories.ListAsync(search, parentId, cursor, limit, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Create([FromBody] CategoryWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await categories.CreateAsync(principal, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPut("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Update(string id, [FromBody] CategoryWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await categories.UpdateAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Delete(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteRequest? request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await categories.DeleteAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}

/// <summary><c>/api/v2/products</c> (contracts/v2/README.md §4). Create requires <c>Idempotency-Key</c>.</summary>
[Route("api/v2/products")]
internal sealed class V2ProductsController(V2ProductService products) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search, [FromQuery] string? categoryId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Reply(await products.ListAsync(search, categoryId, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await products.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Create(
        [FromBody] ProductCreateRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await products.CreateAsync(principal, request, idempotencyKey, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPut("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Update(string id, [FromBody] ProductUpdateRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await products.UpdateAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Archive(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteRequest? request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await products.ArchiveAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}
