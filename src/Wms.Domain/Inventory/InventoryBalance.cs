using Wms.Domain.Catalog;
using Wms.Domain.Common;

namespace Wms.Domain.Inventory;

/// <summary>Before/after snapshot of one balance for the transaction ledger.</summary>
public readonly record struct BalanceChange(int Before, int After)
{
    public int Delta => After - Before;
}

/// <summary>
/// Available stock of one product at one location. Invariant: <see cref="AvailableQuantity"/> is never negative (rule 2).
/// Mutations are internal: callers go through <see cref="InventoryOperations"/>, which enforces the remaining rules.
/// </summary>
public sealed class InventoryBalance
{
    private InventoryBalance(WarehouseId warehouseId, LocationId locationId, ProductId productId, int availableQuantity)
    {
        WarehouseId = warehouseId;
        LocationId = locationId;
        ProductId = productId;
        AvailableQuantity = availableQuantity;
    }

    public WarehouseId WarehouseId { get; }
    public LocationId LocationId { get; }
    public ProductId ProductId { get; }
    public int AvailableQuantity { get; private set; }

    /// <summary>A balance that does not exist yet (first receipt into a location).</summary>
    public static InventoryBalance Empty(WarehouseId warehouseId, LocationId locationId, ProductId productId) =>
        new(warehouseId, locationId, productId, 0);

    /// <summary>Rehydrates a stored balance. A negative stored value means corrupted data and is rejected loudly.</summary>
    public static InventoryBalance Restore(WarehouseId warehouseId, LocationId locationId, ProductId productId, int availableQuantity)
    {
        if (availableQuantity < 0)
        {
            throw new InvalidOperationException(
                $"Stored balance for product {productId} at location {locationId} is negative ({availableQuantity}).");
        }

        return new InventoryBalance(warehouseId, locationId, productId, availableQuantity);
    }

    internal bool Matches(Product product, Location location) =>
        ProductId == product.Id && LocationId == location.Id && WarehouseId == location.WarehouseId;

    internal Result<BalanceChange> Increase(Quantity quantity)
    {
        var before = AvailableQuantity;
        int after;
        try
        {
            after = checked(before + quantity.Value);
        }
        catch (OverflowException)
        {
            return DomainErrors.Validation("Resulting quantity exceeds the supported maximum.");
        }

        AvailableQuantity = after;
        return new BalanceChange(before, after);
    }

    /// <summary>Rule 3: validate available stock before subtracting.</summary>
    internal Result<BalanceChange> Decrease(Quantity quantity, string locationCode)
    {
        if (quantity.Value > AvailableQuantity)
        {
            return DomainErrors.InsufficientStock(locationCode, AvailableQuantity, quantity.Value);
        }

        var before = AvailableQuantity;
        AvailableQuantity -= quantity.Value;
        return new BalanceChange(before, AvailableQuantity);
    }

    internal BalanceChange SetCounted(Quantity counted)
    {
        var before = AvailableQuantity;
        AvailableQuantity = counted.Value;
        return new BalanceChange(before, AvailableQuantity);
    }
}
