using System.Collections.ObjectModel;
using Wms.Domain.Common;

namespace Wms.Domain.Inventory;

/// <summary>
/// One ledger line. <see cref="Quantity"/> is the moved quantity (&gt; 0) for receipts, movements, shipments and
/// adjustments, and the counted quantity (&gt;= 0) for counts. <see cref="From"/>/<see cref="To"/> hold the
/// before/after balance of the source and target location; a count records its location on the To side.
/// </summary>
public sealed record InventoryTransactionLine(
    int LineNo,
    ProductId ProductId,
    LocationId? FromLocationId,
    LocationId? ToLocationId,
    int Quantity,
    BalanceChange? From,
    BalanceChange? To);

/// <summary>Header data supplied by the application layer for a new transaction.</summary>
public sealed record InventoryTransactionContext(
    InventoryTransactionId Id,
    TransactionNumber Number,
    UserId ActorId,
    DateTimeOffset OccurredAt,
    string? Reason = null,
    string? ReferenceNo = null);

/// <summary>A committed inventory transaction. It has no setters: once created it never changes (rule 7).</summary>
public sealed class InventoryTransaction
{
    public const int MaxReasonLength = 500;
    public const int MaxReferenceLength = 64;

    private InventoryTransaction(InventoryTransactionContext context, IReadOnlyList<InventoryTransactionLine> lines)
    {
        Id = context.Id;
        Number = context.Number;
        ActorId = context.ActorId;
        OccurredAt = context.OccurredAt;
        Reason = context.Reason;
        ReferenceNo = context.ReferenceNo;
        Lines = lines;
    }

    public InventoryTransactionId Id { get; }
    public TransactionNumber Number { get; }
    public InventoryTransactionType Type => Number.Type;
    public UserId ActorId { get; }
    public DateTimeOffset OccurredAt { get; }
    public string? Reason { get; }
    public string? ReferenceNo { get; }
    public IReadOnlyList<InventoryTransactionLine> Lines { get; }

    public static Result<InventoryTransaction> Create(
        InventoryTransactionContext context,
        IEnumerable<InventoryTransactionLine> lines)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lines);

        if (context.Number.Value is null)
        {
            return DomainErrors.Validation("Transaction number is required.");
        }

        if (context.OccurredAt.Offset != TimeSpan.Zero)
        {
            return DomainErrors.Validation("Transaction time must be UTC.");
        }

        var reason = string.IsNullOrWhiteSpace(context.Reason) ? null : context.Reason.Trim();
        if (context.Number.Type.RequiresReason() && reason is null)
        {
            return DomainErrors.Validation($"A reason is required for {context.Number.Type} transactions.");
        }

        if (reason is { Length: > MaxReasonLength })
        {
            return DomainErrors.Validation($"Reason cannot exceed {MaxReasonLength} characters.");
        }

        var reference = string.IsNullOrWhiteSpace(context.ReferenceNo) ? null : context.ReferenceNo.Trim();
        if (reference is { Length: > MaxReferenceLength })
        {
            return DomainErrors.Validation($"Reference number cannot exceed {MaxReferenceLength} characters.");
        }

        var lineArray = lines.ToArray();
        if (lineArray.Length == 0)
        {
            return DomainErrors.Validation("A transaction needs at least one line.");
        }

        var minimum = context.Number.Type == InventoryTransactionType.Count ? 0 : 1;
        if (lineArray.Any(l => l.Quantity < minimum))
        {
            return DomainErrors.Validation(minimum == 0
                ? "Counted quantity cannot be negative."
                : "Transaction quantities must be greater than zero.");
        }

        return new InventoryTransaction(
            context with { Reason = reason, ReferenceNo = reference },
            new ReadOnlyCollection<InventoryTransactionLine>(lineArray));
    }
}
