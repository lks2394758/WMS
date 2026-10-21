using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wms.Application.Common;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Inventory;

/// <summary>Who is acting, and the client's Idempotency-Key for this user action.</summary>
public sealed record CommandContext(UserId ActorId, Guid IdempotencyKey);

/// <summary>A committed transaction. <see cref="Replayed"/> is true when the response is the stored original.</summary>
public sealed record InventoryCommandOutcome(InventoryTransactionResponse Response, bool Replayed);

/// <summary>
/// Receipt, movement, shipment, count and adjustment (guide §8). One command = one write scope: the idempotency
/// record is checked, balances are locked, every line runs through <see cref="InventoryOperations"/>, and the
/// balances, ledger transaction, audit entry and idempotency record commit together. Any failure returns before
/// commit, so nothing is stored and a retry is evaluated again. A provider lock timeout or concurrency-token
/// mismatch (<see cref="PersistenceConflictException"/>) becomes <c>CONCURRENCY_CONFLICT</c>; nothing is stored either.
/// </summary>
public sealed class InventoryCommandService(IInventoryStore store, TimeProvider clock)
{
    public const int MaxLines = 200;
    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    public Task<Result<InventoryCommandOutcome>> ReceiveAsync(
        ReceiptRequest request, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.LocationId;
        return ExecuteAsync(
            request,
            context,
            new CommandPlan(
                InventoryTransactionType.Receipt,
                [target],
                ProductIdsOf(request.Lines, l => l.ProductId),
                (i, product, c) => WithQuantity(
                    request.Lines[i].Quantity,
                    q => InventoryOperations.Receive(i + 1, product, c.Location(target), c.Balance(target, product), q)),
                ReferenceNo: request.ReferenceNo),
            cancellationToken);
    }

    public Task<Result<InventoryCommandOutcome>> MoveAsync(
        MovementRequest request, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.FromLocationId;
        var target = request.ToLocationId;
        return ExecuteAsync(
            request,
            context,
            new CommandPlan(
                InventoryTransactionType.Movement,
                [source, target],
                ProductIdsOf(request.Lines, l => l.ProductId),
                (i, product, c) => WithQuantity(
                    request.Lines[i].Quantity,
                    q => InventoryOperations.Move(
                        i + 1,
                        product,
                        c.Location(source),
                        c.Location(target),
                        c.Balance(source, product),
                        c.Balance(target, product),
                        q)),
                ReferenceNo: request.ReferenceNo),
            cancellationToken);
    }

    public Task<Result<InventoryCommandOutcome>> ShipAsync(
        ShipmentRequest request, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.LocationId;
        return ExecuteAsync(
            request,
            context,
            new CommandPlan(
                InventoryTransactionType.Shipment,
                [source],
                ProductIdsOf(request.Lines, l => l.ProductId),
                (i, product, c) => WithQuantity(
                    request.Lines[i].Quantity,
                    q => InventoryOperations.Ship(i + 1, product, c.Location(source), c.Balance(source, product), q)),
                ReferenceNo: request.ReferenceNo),
            cancellationToken);
    }

    public Task<Result<InventoryCommandOutcome>> CountAsync(
        CountRequest request, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = request.LocationId;
        return ExecuteAsync(
            request,
            context,
            new CommandPlan(
                InventoryTransactionType.Count,
                [location],
                ProductIdsOf(request.Lines, l => l.ProductId),
                (i, product, c) =>
                {
                    var counted = Quantity.ForCount(request.Lines[i].CountedQuantity);
                    return counted.IsFailure
                        ? counted.Error!
                        : InventoryOperations.Count(i + 1, product, c.Location(location), c.Balance(location, product), counted.Value);
                },
                Reason: request.Reason),
            cancellationToken);
    }

    public Task<Result<InventoryCommandOutcome>> AdjustAsync(
        AdjustmentRequest request, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = request.LocationId;
        return ExecuteAsync(
            request,
            context,
            new CommandPlan(
                InventoryTransactionType.Adjustment,
                [location],
                ProductIdsOf(request.Lines, l => l.ProductId),
                (i, product, c) => InventoryOperations.Adjust(
                    i + 1, product, c.Location(location), c.Balance(location, product), request.Lines[i].QuantityDelta),
                Reason: request.Reason,
                ReferenceNo: request.ReferenceNo),
            cancellationToken);
    }

    private async Task<Result<InventoryCommandOutcome>> ExecuteAsync<TRequest>(
        TRequest request, CommandContext context, CommandPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IdempotencyKey == Guid.Empty)
        {
            return DomainErrors.Validation($"{ApiHeaders.IdempotencyKey} must be a non-empty UUID.", ApiHeaders.IdempotencyKey);
        }

        var operation = WireNames.Of(plan.Type);
        var requestHash = HashRequest(operation, request);
        var now = clock.GetUtcNow();

        try
        {
            return await ExecuteInScopeAsync(plan, context, operation, requestHash, now, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            // Lock timeout or concurrency-token mismatch: the scope rolled back, so a retry with the same key is safe.
            return ApplicationErrors.ConcurrencyConflict();
        }
    }

    private async Task<Result<InventoryCommandOutcome>> ExecuteInScopeAsync(
        CommandPlan plan,
        CommandContext context,
        string operation,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginWriteAsync(cancellationToken);

        var previous = await scope.FindIdempotencyRecordAsync(context.ActorId, context.IdempotencyKey, now, cancellationToken);
        if (previous is not null)
        {
            if (!string.Equals(previous.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return ApplicationErrors.IdempotencyConflict();
            }

            return new InventoryCommandOutcome(ReadStoredResponse(previous.ResponseJson), Replayed: true);
        }

        var invalid = Validate(plan);
        if (invalid is not null)
        {
            return invalid;
        }

        var productIds = plan.ProductIds!;
        var locations = await scope.GetLocationsAsync(
            plan.LocationIds.Distinct().Select(id => new LocationId(id)).ToList(), cancellationToken);
        foreach (var id in plan.LocationIds)
        {
            if (!locations.ContainsKey(new LocationId(id)))
            {
                return ApplicationErrors.LocationNotFound(id);
            }
        }

        var products = await scope.GetProductsAsync(productIds.Select(id => new ProductId(id)).ToList(), cancellationToken);
        foreach (var id in productIds)
        {
            if (!products.ContainsKey(new ProductId(id)))
            {
                return ApplicationErrors.ProductNotFound(id);
            }
        }

        var keys = locations.Values
            .SelectMany(location => products.Values.Select(product => new BalanceKey(location.WarehouseId, location.Id, product.Id)))
            .ToList();
        var balances = await scope.LockBalancesAsync(keys, cancellationToken);
        var lineContext = new LineContext(locations, balances);

        var lines = new List<InventoryTransactionLine>(productIds.Count);
        for (var i = 0; i < productIds.Count; i++)
        {
            var line = plan.ApplyLine(i, products[new ProductId(productIds[i])], lineContext);
            if (line.IsFailure)
            {
                return line.Error!; // not committed: the scope discards every balance it loaded
            }

            lines.Add(line.Value);
        }

        // Business date is the UTC date until the open decision in docs/architecture.md is taken.
        var number = await scope.AllocateTransactionNumberAsync(plan.Type, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var transaction = InventoryTransaction.Create(
            new InventoryTransactionContext(InventoryTransactionId.New(), number, context.ActorId, now, plan.Reason, plan.ReferenceNo),
            lines);
        if (transaction.IsFailure)
        {
            return transaction.Error!;
        }

        var response = ContractMapper.ToResponse(
            transaction.Value,
            TouchedBalances(lines, locations, balances).Select(b => ContractMapper.ToResponse(b, now)).ToList());

        scope.Add(transaction.Value);
        scope.Add(new AuditEntry(
            Guid.CreateVersion7(), now, context.ActorId, $"inventory.{operation}", "InventoryTransaction", number.Value));
        scope.Add(new IdempotencyRecord(
            context.ActorId,
            context.IdempotencyKey,
            operation,
            requestHash,
            JsonSerializer.Serialize(response, WmsJson.Options),
            now,
            now + IdempotencyRetention));
        await scope.CommitAsync(now, cancellationToken);

        return new InventoryCommandOutcome(response, Replayed: false);
    }

    private static DomainError? Validate(CommandPlan plan)
    {
        if (plan.ProductIds is not { Count: > 0 } productIds)
        {
            return DomainErrors.Validation("lines must contain at least one line.", "lines");
        }

        if (productIds.Count > MaxLines)
        {
            return DomainErrors.Validation($"A request can contain at most {MaxLines} lines.", "lines");
        }

        if (productIds.Distinct().Count() != productIds.Count)
        {
            return DomainErrors.Validation("Each product can appear only once per request.", "lines");
        }

        if (plan.Type.RequiresReason() && string.IsNullOrWhiteSpace(plan.Reason))
        {
            return DomainErrors.Validation($"reason is required for {WireNames.Of(plan.Type)} transactions.", "reason");
        }

        return null;
    }

    /// <summary>Each balance a line touched, in line order (source before target), without duplicates.</summary>
    private static List<InventoryBalance> TouchedBalances(
        IEnumerable<InventoryTransactionLine> lines,
        IReadOnlyDictionary<LocationId, Location> locations,
        IReadOnlyDictionary<BalanceKey, InventoryBalance> balances)
    {
        var touched = new List<InventoryBalance>();
        foreach (var line in lines)
        {
            foreach (var locationId in new[] { line.FromLocationId, line.ToLocationId })
            {
                if (locationId is { } id)
                {
                    var balance = balances[new BalanceKey(locations[id].WarehouseId, id, line.ProductId)];
                    if (!touched.Contains(balance))
                    {
                        touched.Add(balance);
                    }
                }
            }
        }

        return touched;
    }

    /// <summary>Null when the list or one of its lines is missing; validation then rejects the request.</summary>
    private static List<Guid>? ProductIdsOf<TLine>(IReadOnlyList<TLine>? lines, Func<TLine, Guid> productId)
        where TLine : class =>
        lines is null || lines.Any(l => l is null) ? null : lines.Select(productId).ToList();

    private static Result<InventoryTransactionLine> WithQuantity(
        int value, Func<Quantity, Result<InventoryTransactionLine>> apply)
    {
        var quantity = Quantity.ForTransaction(value);
        return quantity.IsFailure ? quantity.Error! : apply(quantity.Value);
    }

    /// <summary>Covers the operation and the exact request body: the same key with any other body is a conflict.</summary>
    private static string HashRequest<TRequest>(string operation, TRequest request)
    {
        var canonical = operation + "\n" + JsonSerializer.Serialize(request, WmsJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static InventoryTransactionResponse ReadStoredResponse(string json) =>
        JsonSerializer.Deserialize<InventoryTransactionResponse>(json, WmsJson.Options)
        ?? throw new InvalidOperationException("Stored idempotent response is empty.");

    private sealed record CommandPlan(
        InventoryTransactionType Type,
        IReadOnlyList<Guid> LocationIds,
        IReadOnlyList<Guid>? ProductIds,
        Func<int, Product, LineContext, Result<InventoryTransactionLine>> ApplyLine,
        string? Reason = null,
        string? ReferenceNo = null);

    private sealed class LineContext(
        IReadOnlyDictionary<LocationId, Location> locations,
        IReadOnlyDictionary<BalanceKey, InventoryBalance> balances)
    {
        public Location Location(Guid id) => locations[new LocationId(id)];

        public InventoryBalance Balance(Guid locationId, Product product)
        {
            var location = Location(locationId);
            return balances[new BalanceKey(location.WarehouseId, location.Id, product.Id)];
        }
    }
}
