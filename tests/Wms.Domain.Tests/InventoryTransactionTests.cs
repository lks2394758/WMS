using System.Reflection;
using Wms.Domain.Common;
using Wms.Domain.Inventory;
using static Wms.Domain.Tests.Fixtures;

namespace Wms.Domain.Tests;

public class InventoryTransactionTests
{
    [Fact]
    public void Committed_transaction_exposes_no_public_setters()
    {
        var settable = typeof(InventoryTransaction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true });

        Assert.Empty(settable);
    }

    [Fact]
    public void Lines_cannot_be_modified_after_creation()
    {
        var transaction = InventoryTransaction.Create(Context(InventoryTransactionType.Receipt), [AnyLine()]).Value;

        var asList = Assert.IsAssignableFrom<IList<InventoryTransactionLine>>(transaction.Lines);
        Assert.Throws<NotSupportedException>(() => asList.Add(AnyLine()));
    }

    [Fact]
    public void Count_with_reason_is_accepted_and_reason_is_trimmed()
    {
        var result = InventoryTransaction.Create(Context(InventoryTransactionType.Count, "  cycle count  "), [AnyLine(0)]);

        Assert.True(result.IsSuccess);
        Assert.Equal("cycle count", result.Value.Reason);
        Assert.Equal(InventoryTransactionType.Count, result.Value.Type);
    }

    [Fact]
    public void Transaction_requires_at_least_one_line()
    {
        var result = InventoryTransaction.Create(Context(InventoryTransactionType.Receipt), []);

        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void Non_count_line_with_zero_quantity_is_rejected()
    {
        var result = InventoryTransaction.Create(Context(InventoryTransactionType.Shipment), [AnyLine(0)]);

        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void Transaction_time_must_be_utc()
    {
        var context = Context(InventoryTransactionType.Receipt) with
        {
            OccurredAt = new DateTimeOffset(2026, 10, 10, 11, 0, 0, TimeSpan.FromHours(-7)),
        };

        var result = InventoryTransaction.Create(context, [AnyLine()]);

        Assert.Equal(DomainErrorCodes.ValidationFailed, result.Error!.Code);
    }
}
