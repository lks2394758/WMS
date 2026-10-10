namespace Wms.Contracts.Inventory;

public sealed record InventoryBalanceResponse(
    Guid WarehouseId,
    Guid LocationId,
    Guid ProductId,
    int AvailableQuantity,
    DateTimeOffset UpdatedAt);

/// <summary>Before/after of one location's balance on a ledger line.</summary>
public sealed record BalanceSnapshot(Guid LocationId, int Before, int After);

public sealed record TransactionLine(
    int LineNo,
    Guid ProductId,
    int Quantity,
    BalanceSnapshot? From = null,
    BalanceSnapshot? To = null);

/// <summary>Returned by every inventory write and by the transaction history.</summary>
public sealed record InventoryTransactionResponse(
    string TransactionNo,
    string Type,
    DateTimeOffset OccurredAt,
    Guid ActorId,
    IReadOnlyList<TransactionLine> Lines,
    IReadOnlyList<InventoryBalanceResponse> Balances,
    string? Reason = null,
    string? ReferenceNo = null);

/// <summary>Transaction type names as they appear on the wire.</summary>
public static class TransactionTypes
{
    public const string Receipt = "receipt";
    public const string Movement = "movement";
    public const string Shipment = "shipment";
    public const string Count = "count";
    public const string Adjustment = "adjustment";
}
