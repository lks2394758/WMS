using Wms.Domain.V2.Inventory;

namespace Wms.Domain.Tests;

/// <summary>v2 exact quantities and the bin rules of one inventory transaction (pure domain, no storage).</summary>
public class V2InventoryRulesTests
{
    private const long Unit = V2Micro.PerUnit;
    private const long DefaultLow = 20 * Unit;
    private const long DefaultHigh = 80 * Unit;

    [Theory]
    [InlineData(2 * Unit, 50 * Unit, MultiplyOutcome.Exact, 100 * Unit)]
    [InlineData(2_500_000, 400_000, MultiplyOutcome.Exact, 1_000_000)]                   // 2.5 × 0.4 = 1
    [InlineData(1_500_000, 333_333, MultiplyOutcome.NeedsRounding, 0)]                  // 0.4999995: 7 decimals
    [InlineData(V2Micro.Max, Unit, MultiplyOutcome.Exact, V2Micro.Max)]                   // 128-bit intermediate
    [InlineData(V2Micro.Max, 2 * Unit, MultiplyOutcome.OutOfRange, 0)]
    [InlineData(999_999_999_999_000_000, 1_000_000_000_000_000_000, MultiplyOutcome.OutOfRange, 0)]  // 10^35 intermediate
    [InlineData(V2Micro.Max, V2Micro.Max, MultiplyOutcome.NeedsRounding, 0)]                          // exact check first
    public void Packaging_products_are_exact_or_rejected(long a, long b, MultiplyOutcome outcome, long expected)
    {
        Assert.Equal(outcome, V2Micro.Multiply(a, b, out var product));
        Assert.Equal(expected, product);
    }

    [Theory]
    [InlineData(1_000_000, 0, true)]
    [InlineData(1_500_000, 0, false)]
    [InlineData(1_250_000, 2, true)]
    [InlineData(1_255_000, 2, false)]
    [InlineData(1, 6, true)]
    public void Precision_is_checked_on_the_value(long micros, int decimals, bool fits)
    {
        Assert.Equal(fits, V2Micro.FitsPrecision(micros, decimals));
    }

    [Fact]
    public void An_empty_bin_needs_a_capacity_and_takes_the_product_and_overrides()
    {
        var bin = Bin(product: null, quantity: 0);

        Assert.Equal(V2InventoryCodes.CapacityRequired, bin.Add(7, 0, 5 * Unit, null, null, null, DefaultLow, DefaultHigh, "lines[0]")!.Code);
        Assert.Equal(V2InventoryCodes.PrecisionExceeded, bin.Add(7, 0, 5 * Unit, 10_500_000, null, null, DefaultLow, DefaultHigh, "lines[0]")!.Code);
        Assert.Equal(
            V2InventoryCodes.ValidationFailed,
            bin.Add(7, 0, 5 * Unit, 10 * Unit, 50 * Unit, null, DefaultLow, 40 * Unit, "lines[0]")!.Code); // effective low ≥ high

        Assert.Null(bin.Add(7, 0, 5 * Unit, 10 * Unit, null, 90 * Unit, DefaultLow, DefaultHigh, "lines[0]"));
        Assert.Equal((7L, 5 * Unit, 10 * Unit, (long?)null, (long?)(90 * Unit), true),
            (bin.ProductId!.Value, bin.QuantityMicros, bin.CapacityMicros!.Value, bin.LowThresholdMicros, bin.HighThresholdMicros, bin.Changed));
    }

    [Fact]
    public void A_bin_with_stock_keeps_its_configuration_and_its_product()
    {
        var bin = Bin(product: 7, quantity: 5 * Unit, capacity: 10 * Unit);

        Assert.Equal(V2InventoryCodes.CapacityNotAllowed, bin.Add(7, 0, Unit, 20 * Unit, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Equal(V2InventoryCodes.BinProductMismatch, bin.Add(8, 0, Unit, null, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Equal(V2InventoryCodes.BinProductMismatch, bin.Remove(8, Unit, "l", "fromBinId")!.Code);
        Assert.Null(bin.Add(7, 0, 20 * Unit, null, null, null, DefaultLow, DefaultHigh, "l")); // over capacity is allowed
        Assert.Equal(25 * Unit, bin.QuantityMicros);
    }

    [Fact]
    public void Stock_never_goes_negative_and_an_emptied_bin_keeps_product_and_capacity_but_needs_a_new_capacity()
    {
        var bin = Bin(product: 7, quantity: 5 * Unit, capacity: 10 * Unit, low: 30 * Unit);

        Assert.Equal(V2InventoryCodes.InsufficientStock, bin.Remove(7, 6 * Unit, "l", "fromBinId")!.Code);
        Assert.Null(bin.Remove(7, 5 * Unit, "l", "fromBinId"));
        Assert.Equal((7L, 0L, 10 * Unit), (bin.ProductId!.Value, bin.QuantityMicros, bin.CapacityMicros!.Value));
        Assert.Equal(V2InventoryCodes.InsufficientStock, bin.Remove(7, 1, "l", "fromBinId")!.Code);

        Assert.Equal(V2InventoryCodes.CapacityRequired, bin.Add(7, 0, Unit, null, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Null(bin.Add(8, 0, Unit, 4 * Unit, null, null, DefaultLow, DefaultHigh, "l")); // another product may enter an empty bin
        Assert.Equal((8L, (long?)null), (bin.ProductId!.Value, bin.LowThresholdMicros)); // old override not carried over
    }

    [Fact]
    public void An_unresolved_investigation_keeps_other_products_out_of_an_empty_bin()
    {
        var bin = Bin(product: 7, quantity: 0, capacity: 10 * Unit, investigation: true);

        Assert.Equal(V2InventoryCodes.InvestigationUnresolved, bin.Add(8, 0, Unit, 5 * Unit, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Null(bin.Add(7, 0, Unit, 5 * Unit, null, null, DefaultLow, DefaultHigh, "l")); // its own product still moves
    }

    [Fact]
    public void Frozen_and_archived_bins_accept_nothing_and_the_largest_quantity_is_enforced()
    {
        Assert.Equal(V2InventoryCodes.BinFrozen, Bin(product: 7, quantity: Unit, frozen: true).Remove(7, Unit, "l", "fromBinId")!.Code);
        Assert.Equal(V2InventoryCodes.BinFrozen, Bin(product: null, quantity: 0, frozen: true).Add(7, 0, Unit, Unit, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Equal(V2InventoryCodes.Archived, Bin(product: null, quantity: 0, archived: true).Add(7, 0, Unit, Unit, null, null, DefaultLow, DefaultHigh, "l")!.Code);

        var full = Bin(product: 7, quantity: V2Micro.Max, capacity: Unit);
        Assert.Equal(V2InventoryCodes.QuantityOutOfRange, full.Add(7, 6, 1, null, null, null, DefaultLow, DefaultHigh, "l")!.Code);
        Assert.Equal(V2Micro.Max, full.QuantityMicros);
    }

    private static V2BinState Bin(
        long? product, long quantity, long? capacity = null, long? low = null, bool frozen = false, bool archived = false, bool investigation = false) =>
        new(1, "A-R1-01-01", product, quantity, capacity ?? (quantity > 0 ? 100 * Unit : null), low, null, archived, frozen, investigation, 1);
}
