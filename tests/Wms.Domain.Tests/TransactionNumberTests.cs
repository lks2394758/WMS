using Wms.Domain.Inventory;

namespace Wms.Domain.Tests;

public class TransactionNumberTests
{
    [Theory]
    [InlineData(InventoryTransactionType.Receipt, "RCV-20261010-000001")]
    [InlineData(InventoryTransactionType.Movement, "MOV-20261010-000001")]
    [InlineData(InventoryTransactionType.Shipment, "SHP-20261010-000001")]
    [InlineData(InventoryTransactionType.Count, "CNT-20261010-000001")]
    [InlineData(InventoryTransactionType.Adjustment, "ADJ-20261010-000001")]
    public void Number_uses_type_prefix_date_and_six_digit_sequence(InventoryTransactionType type, string expected)
    {
        var number = TransactionNumber.Create(type, new DateOnly(2026, 10, 10), 1);

        Assert.Equal(expected, number.Value);
    }

    [Fact]
    public void Number_round_trips_through_parse()
    {
        var number = TransactionNumber.Create(InventoryTransactionType.Shipment, new DateOnly(2026, 12, 31), 999_999);

        Assert.True(TransactionNumber.TryParse(number.Value, out var parsed));
        Assert.Equal(number, parsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void Sequence_outside_range_is_rejected(int sequence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransactionNumber.Create(InventoryTransactionType.Receipt, new DateOnly(2026, 10, 10), sequence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("XYZ-20261010-000001")]
    [InlineData("RCV-20261310-000001")]
    [InlineData("RCV-20261010-000000")]
    [InlineData("RCV-20261010-00001")]
    public void Invalid_numbers_do_not_parse(string text)
    {
        Assert.False(TransactionNumber.TryParse(text, out _));
    }
}
