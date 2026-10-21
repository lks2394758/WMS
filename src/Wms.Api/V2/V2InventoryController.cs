using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Application.V2.Inventory;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Inventory;

namespace Wms.Api.V2;

/// <summary>
/// <c>/api/v2/inventory</c> (contracts/v2/README.md §2, §7): the four operations (each with its permission and a required
/// <c>Idempotency-Key</c>) and the immutable transaction history. Binding and result mapping only.
/// </summary>
[Route("api/v2/inventory")]
internal sealed class V2InventoryController(V2InventoryService inventory, V2TransactionQueries transactions) : V2ControllerBase
{
    [HttpPost("receipts")]
    [Authorize(Policy = PermissionNames.InventoryReceive)]
    public Task<IActionResult> Receive(
        [FromBody] InventoryOperationRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        ExecuteAsync(TransactionTypesV2.Receipt, request, key, cancellationToken);

    [HttpPost("shipments")]
    [Authorize(Policy = PermissionNames.InventoryShip)]
    public Task<IActionResult> Ship(
        [FromBody] InventoryOperationRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        ExecuteAsync(TransactionTypesV2.Shipment, request, key, cancellationToken);

    [HttpPost("movements")]
    [Authorize(Policy = PermissionNames.InventoryMove)]
    public Task<IActionResult> Move(
        [FromBody] InventoryOperationRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        ExecuteAsync(TransactionTypesV2.Movement, request, key, cancellationToken);

    [HttpPost("adjustments")]
    [Authorize(Policy = PermissionNames.InventoryAdjust)]
    public Task<IActionResult> Adjust(
        [FromBody] InventoryOperationRequest request, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, CancellationToken cancellationToken) =>
        ExecuteAsync(TransactionTypesV2.Adjustment, request, key, cancellationToken);

    [HttpGet("transactions")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? type,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? productId,
        [FromQuery] string? binId,
        [FromQuery] string? actorId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await transactions.ListAsync(search, type, from, to, productId, binId, actorId, cursor, limit, cancellationToken));

    [HttpGet("transactions/{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await transactions.GetAsync(id, cancellationToken));

    private async Task<IActionResult> ExecuteAsync(string type, InventoryOperationRequest request, string? key, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await inventory.ExecuteAsync(type, principal, request, key, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();
}
