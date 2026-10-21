using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Application.V2.Counts;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Counts;

namespace Wms.Api.V2;

/// <summary>
/// <c>/api/v2/counts</c> (contracts/v2/README.md §2, §9): list, detail, start, submit, confirm, recount, cancel, resolve.
/// Binding and result mapping only.
/// </summary>
[Route("api/v2/counts")]
internal sealed class V2CountsController(V2CountService counts) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? binId,
        [FromQuery] bool? unresolved,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await counts.ListAsync(search, status, binId, unresolved, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await counts.GetAsync(Principal, id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Start(
        [FromBody] CountStartRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await counts.StartAsync(principal, request, key, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPost("{id}/submit")]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Submit(string id, [FromBody] CountSubmitRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await counts.SubmitAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpPost("{id}/confirm")]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Confirm(
        string id, [FromBody] CountConfirmRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await counts.ConfirmAsync(principal, id, request, key, cancellationToken)) : Unauthenticated();

    [HttpPost("{id}/resolve")]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Resolve(string id, [FromBody] CountResolveRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await counts.ResolveAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpPost("{id}/recount")]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Recount(string id, [FromBody] CountRecountRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await counts.RecountAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpPost("{id}/cancel")]
    [Authorize(Policy = PermissionNames.InventoryCount)]
    public async Task<IActionResult> Cancel(string id, [FromBody] CountCancelRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await counts.CancelAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}
