namespace Wms.Domain.Inventory;

public enum InventoryTransactionType
{
    Receipt = 1,
    Movement = 2,
    Shipment = 3,
    Count = 4,
    Adjustment = 5,
}

public static class InventoryTransactionTypeExtensions
{
    /// <summary>Transaction-number prefix (guide §8.2).</summary>
    public static string Prefix(this InventoryTransactionType type) => type switch
    {
        InventoryTransactionType.Receipt => "RCV",
        InventoryTransactionType.Movement => "MOV",
        InventoryTransactionType.Shipment => "SHP",
        InventoryTransactionType.Count => "CNT",
        InventoryTransactionType.Adjustment => "ADJ",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown transaction type."),
    };

    /// <summary>Counts and manual adjustments require a non-empty reason (rule 6).</summary>
    public static bool RequiresReason(this InventoryTransactionType type) =>
        type is InventoryTransactionType.Count or InventoryTransactionType.Adjustment;
}
