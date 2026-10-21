using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Inventory;

namespace Wms.Api.V2;

/// <summary><c>/api/v2/serialized-items</c> (contracts/v2/README.md §2, §8). Binding and result mapping only.</summary>
[Route("api/v2/serialized-items")]
internal sealed class V2SerializedItemsController(V2SerialService serials) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? productId,
        [FromQuery] string? binId,
        [FromQuery] bool? inStock,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await serials.ListAsync(search, productId, binId, inStock, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await serials.GetAsync(id, cancellationToken));

    [HttpPost("{id}/correct")]
    [Authorize(Policy = PermissionNames.SerialNumbersCorrect)]
    public async Task<IActionResult> Correct(string id, [FromBody] SerialCorrectionRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await serials.CorrectAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}
