namespace Wms.Domain.Inventory;

public enum InventoryAlertState
{
    /// <summary>Capacity is missing or non-positive: no alert is calculated.</summary>
    NotConfigured = 0,
    Normal = 1,
    LowStock = 2,
    Overstock = 3,
}

/// <summary>Rule 8: warehouse-level product stock against configured capacity.</summary>
public static class StockAlertPolicy
{
    public const decimal LowStockThreshold = 0.20m;
    public const decimal OverstockThreshold = 0.80m;

    /// <param name="totalAvailable">Sum of available quantity for the product across all locations of the warehouse.</param>
    /// <param name="capacity">The product's configured capacity.</param>
    public static InventoryAlertState Evaluate(int totalAvailable, int? capacity)
    {
        if (capacity is null or <= 0)
        {
            return InventoryAlertState.NotConfigured;
        }

        var ratio = (decimal)totalAvailable / capacity.Value;
        if (ratio < LowStockThreshold)
        {
            return InventoryAlertState.LowStock;
        }

        return ratio > OverstockThreshold ? InventoryAlertState.Overstock : InventoryAlertState.Normal;
    }
}
