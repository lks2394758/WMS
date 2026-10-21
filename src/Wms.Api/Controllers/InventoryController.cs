using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Api.Auth;
using Wms.Api.Validation;
using Wms.Application.Common;
using Wms.Application.Inventory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Common;

namespace Wms.Api.Controllers;

/// <summary>
/// Balance reads and the five inventory writes. Every write needs an <c>Idempotency-Key</c> UUID; a replay returns
/// the stored original with <c>Idempotent-Replayed: true</c>.
/// </summary>
[Route("api/v1/inventory")]
public sealed class InventoryController(InventoryQueries queries, InventoryCommandService commands) : WmsControllerBase
{
    /// <summary>Committed balances, optionally filtered by product and/or location.</summary>
    [HttpGet("balances")]
    [ProducesResponseType<CursorPage<InventoryBalanceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    public async Task<ActionResult<CursorPage<InventoryBalanceResponse>>> Balances(
        [FromQuery] Guid? productId,
        [FromQuery] Guid? locationId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        FromResult(await queries.ListBalancesAsync(productId, locationId, cursor, limit, cancellationToken));

    [HttpPost("receipts")]
    [Authorize(Policy = Permissions.InventoryReceive)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<InventoryTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemJson)]
    public Task<ActionResult<InventoryTransactionResponse>> Receive(
        ReceiptRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(idempotencyKey, RequestRules.Validate(request), context => commands.ReceiveAsync(request, context, cancellationToken));

    /// <summary>Moves stock between two locations. Put-away and relocation are receiving-side work (Inventory.Receive).</summary>
    [HttpPost("movements")]
    [Authorize(Policy = Permissions.InventoryReceive)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<InventoryTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemJson)]
    public Task<ActionResult<InventoryTransactionResponse>> Move(
        MovementRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(idempotencyKey, RequestRules.Validate(request), context => commands.MoveAsync(request, context, cancellationToken));

    [HttpPost("shipments")]
    [Authorize(Policy = Permissions.InventoryShip)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<InventoryTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemJson)]
    public Task<ActionResult<InventoryTransactionResponse>> Ship(
        ShipmentRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(idempotencyKey, RequestRules.Validate(request), context => commands.ShipAsync(request, context, cancellationToken));

    [HttpPost("counts")]
    [Authorize(Policy = Permissions.InventoryCount)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<InventoryTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemJson)]
    public Task<ActionResult<InventoryTransactionResponse>> Count(
        CountRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(idempotencyKey, RequestRules.Validate(request), context => commands.CountAsync(request, context, cancellationToken));

    [HttpPost("adjustments")]
    [Authorize(Policy = Permissions.InventoryAdjust)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)]
    [ProducesResponseType<InventoryTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemJson)]
    public Task<ActionResult<InventoryTransactionResponse>> Adjust(
        AdjustmentRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(idempotencyKey, RequestRules.Validate(request), context => commands.AdjustAsync(request, context, cancellationToken));

    private async Task<ActionResult<InventoryTransactionResponse>> ExecuteAsync(
        string? idempotencyKey, FieldErrors requestErrors, Func<CommandContext, Task<Result<InventoryCommandOutcome>>> command)
    {
        var userId = User.FindUserId();
        if (userId is null)
        {
            return Failure(ApplicationErrors.Unauthenticated("The access token has no user."));
        }

        // Every invalid field, header included, is reported in one 400 before the use case runs.
        var invalid = RequestRules.IdempotencyKey(idempotencyKey).Merge(requestErrors);
        if (!invalid.IsEmpty)
        {
            return ValidationFailure(invalid);
        }

        var result = await command(new CommandContext(userId.Value, Guid.Parse(idempotencyKey!)));
        if (result.IsFailure)
        {
            return Failure(result.Error!);
        }

        if (result.Value.Replayed)
        {
            Response.Headers[ApiHeaders.IdempotentReplayed] = "true";
        }

        return Ok(result.Value.Response);
    }
}
