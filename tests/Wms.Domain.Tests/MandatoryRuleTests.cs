using Wms.Domain.Common;
using Wms.Domain.Inventory;
using static Wms.Domain.Tests.Fixtures;

namespace Wms.Domain.Tests;

/// <summary>The mandatory tests named in guide §5.3.</summary>
public class MandatoryRuleTests
{
    public static TheoryData<string?> BlankReasons => new() { null, "", "   " };

    public static TheoryData<int?> UnconfiguredCapacities => new() { null, 0, -10 };

    [Fact]
    public void Cannot_ship_more_than_available_quantity()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, available: 2);

        var result = InventoryOperations.Ship(1, product, location, balance, Qty(3));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCodes.InsufficientStock, result.Error!.Code);
        Assert.Equal(2, balance.AvailableQuantity);
    }

    [Fact]
    public void Cannot_move_to_the_same_location()
    {
        var product = NewProduct();
        var location = NewLocation();
        var balance = BalanceOf(product, location, available: 5);

        var result = InventoryOperations.Move(1, product, location, location, balance, balance, Qty(1));

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal(5, balance.AvailableQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Cannot_create_zero_or_negative_transaction_quantity(int value)
    {
        var quantity = Quantity.ForTransaction(value);

        Assert.True(quantity.IsFailure);
        Assert.Equal(DomainErrorCodes.ValidationFailed, quantity.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(BlankReasons))]
    public void Count_requires_a_reason(string? reason)
    {
        var result = InventoryTransaction.Create(Context(InventoryTransactionType.Count, reason), [AnyLine()]);

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Theory]
    [MemberData(nameof(BlankReasons))]
    public void Adjustment_requires_a_reason(string? reason)
    {
        var result = InventoryTransaction.Create(Context(InventoryTransactionType.Adjustment, reason), [AnyLine()]);

        Assert.True(result.IsFailure);
        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(19, 100)]
    [InlineData(1, 6)]
    public void Low_stock_is_triggered_below_twenty_percent(int total, int capacity)
    {
        Assert.Equal(InventoryAlertState.LowStock, StockAlertPolicy.Evaluate(total, capacity));
    }

    [Theory]
    [InlineData(81, 100)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void Overstock_is_triggered_above_eighty_percent(int total, int capacity)
    {
        Assert.Equal(InventoryAlertState.Overstock, StockAlertPolicy.Evaluate(total, capacity));
    }

    [Theory]
    [MemberData(nameof(UnconfiguredCapacities))]
    public void Capacity_not_configured_does_not_trigger_alert(int? capacity)
    {
        Assert.Equal(InventoryAlertState.NotConfigured, StockAlertPolicy.Evaluate(0, capacity));
    }
}
