using Wms.Domain.Common;
using Wms.Domain.Inventory;
using static Wms.Domain.Tests.Fixtures;

namespace Wms.Domain.Tests;

public class InventoryOperationsTests
{
    [Fact]
    public void Receive_increases_balance_and_records_target_snapshot()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = InventoryBalance.Empty(Warehouse, location.Id, product.Id);

        var line = InventoryOperations.Receive(1, product, location, balance, Qty(10)).Value;

        Assert.Equal(10, balance.AvailableQuantity);
        Assert.Equal(location.Id, line.ToLocationId);
        Assert.Null(line.FromLocationId);
        Assert.Equal<BalanceChange?>(new BalanceChange(0, 10), line.To);
    }

    [Fact]
    public void Move_updates_both_balances_and_records_both_snapshots()
    {
        var product = NewProduct();
        var source = NewLocation("A-01-01");
        var target = NewLocation("B-02-01");
        var sourceBalance = BalanceOf(product, source, 7);
        var targetBalance = BalanceOf(product, target, 1);

        var line = InventoryOperations.Move(1, product, source, target, sourceBalance, targetBalance, Qty(4)).Value;

        Assert.Equal(3, sourceBalance.AvailableQuantity);
        Assert.Equal(5, targetBalance.AvailableQuantity);
        Assert.Equal<BalanceChange?>(new BalanceChange(7, 3), line.From);
        Assert.Equal<BalanceChange?>(new BalanceChange(1, 5), line.To);
    }

    [Fact]
    public void Move_with_insufficient_source_stock_changes_neither_balance()
    {
        var product = NewProduct();
        var source = NewLocation("A-01-01");
        var target = NewLocation("B-02-01");
        var sourceBalance = BalanceOf(product, source, 2);
        var targetBalance = BalanceOf(product, target, 1);

        var result = InventoryOperations.Move(1, product, source, target, sourceBalance, targetBalance, Qty(3));

        Assert.Equal(DomainErrorCodes.InsufficientStock, result.Error!.Code);
        Assert.Equal(2, sourceBalance.AvailableQuantity);
        Assert.Equal(1, targetBalance.AvailableQuantity);
    }

    [Fact]
    public void Shipping_the_last_unit_leaves_zero()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, 1);

        var line = InventoryOperations.Ship(1, product, location, balance, Qty(1)).Value;

        Assert.Equal(0, balance.AvailableQuantity);
        Assert.Equal<BalanceChange?>(new BalanceChange(1, 0), line.From);
    }

    [Fact]
    public void Disabled_product_cannot_be_used()
    {
        var product = NewProduct();
        product.Deactivate();
        var location = NewLocation();
        var balance = BalanceOf(product, location, 5);

        var result = InventoryOperations.Ship(1, product, location, balance, Qty(1));

        Assert.Equal(DomainErrorCodes.ProductInactive, result.Error!.Code);
        Assert.Equal(5, balance.AvailableQuantity);
    }

    [Fact]
    public void Disabled_location_cannot_be_used()
    {
        var product = NewProduct();
        var location = NewLocation();
        location.Deactivate();
        var balance = InventoryBalance.Empty(Warehouse, location.Id, product.Id);

        var result = InventoryOperations.Receive(1, product, location, balance, Qty(1));

        Assert.Equal(DomainErrorCodes.LocationInactive, result.Error!.Code);
        Assert.Equal(0, balance.AvailableQuantity);
    }

    [Fact]
    public void Count_may_set_zero_and_records_counted_quantity()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, 4);

        var line = InventoryOperations.Count(1, product, location, balance, Quantity.ForCount(0).Value).Value;

        Assert.Equal(0, balance.AvailableQuantity);
        Assert.Equal(0, line.Quantity);
        Assert.Equal<BalanceChange?>(new BalanceChange(4, 0), line.To);
    }

    [Fact]
    public void Counted_quantity_cannot_be_negative()
    {
        Assert.True(Quantity.ForCount(-1).IsFailure);
    }

    [Fact]
    public void Negative_adjustment_cannot_go_below_zero()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, 2);

        var result = InventoryOperations.Adjust(1, product, location, balance, -3);

        Assert.Equal(DomainErrorCodes.InsufficientStock, result.Error!.Code);
        Assert.Equal(2, balance.AvailableQuantity);
    }

    [Fact]
    public void Positive_adjustment_records_target_side()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, 2);

        var line = InventoryOperations.Adjust(1, product, location, balance, 5).Value;

        Assert.Equal(7, balance.AvailableQuantity);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(location.Id, line.ToLocationId);
    }

    [Fact]
    public void Zero_adjustment_is_rejected()
    {
        var product = NewProduct();
        var location = NewLocation();

        var result = InventoryOperations.Adjust(1, product, location, BalanceOf(product, location, 2), 0);

        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void Balance_for_another_product_is_a_programming_error()
    {
        var product = NewProduct();
        var other = NewProduct();
        var location = NewLocation();

        Assert.Throws<ArgumentException>(() =>
            InventoryOperations.Ship(1, product, location, BalanceOf(other, location, 5), Qty(1)));
    }

    [Fact]
    public void Stored_negative_balance_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InventoryBalance.Restore(Warehouse, LocationId.New(), ProductId.New(), -1));
    }
}
