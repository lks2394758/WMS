using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>The request-wide serial item limit (entered plus generated over all lines), checked before any database work.</summary>
public class V2InventoryRequestParserTests
{
    [Fact]
    public void Exactly_10000_entered_and_generated_items_are_accepted()
    {
        var request = new InventoryOperationRequest(
        [
            Generate(4_000), Generate(5_000), Manual(Enumerable.Range(1, 1_000).Select(i => $"M-{i}").ToArray()),
        ]);

        Assert.Null(V2InventoryRequestParser.Parse(TransactionTypesV2.Receipt, request, out var plan));
        Assert.Equal(3, plan.Lines.Count);
    }

    [Fact]
    public void Several_generation_lines_over_the_total_are_rejected()
    {
        var failure = V2InventoryRequestParser.Parse(
            TransactionTypesV2.Receipt, new InventoryOperationRequest([Generate(5_000), Generate(5_001)]), out _);

        Assert.Equal((ProblemCodesV2.ValidationFailed, "lines[1]"), (failure!.Code, failure.Field));
    }

    [Fact]
    public void Entered_plus_generated_items_over_the_total_are_rejected_in_either_order()
    {
        var manualFirst = V2InventoryRequestParser.Parse(
            TransactionTypesV2.Receipt, new InventoryOperationRequest([Manual("A-1", "A-2"), Generate(9_999)]), out _);
        var generatedFirst = V2InventoryRequestParser.Parse(
            TransactionTypesV2.Receipt, new InventoryOperationRequest([Generate(9_999), Manual("A-1", "A-2")]), out _);

        Assert.Equal((ProblemCodesV2.ValidationFailed, "lines[1]"), (manualFirst!.Code, manualFirst.Field));
        Assert.Equal(ProblemCodesV2.ValidationFailed, generatedFirst!.Code);
        Assert.StartsWith("lines[1]", generatedFirst.Field, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_oversized_manual_list_is_rejected_while_it_is_read()
    {
        var failure = V2InventoryRequestParser.Parse(
            TransactionTypesV2.Receipt,
            new InventoryOperationRequest([Manual(Enumerable.Range(1, 10_001).Select(i => $"S-{i}").ToArray())]),
            out _);

        Assert.Equal((ProblemCodesV2.ValidationFailed, "lines[0].serialNumbers"), (failure!.Code, failure.Field));
    }

    private static InventoryLine Generate(int count) => new(ProductId: "1", ToBinId: "1", GenerateSerialCount: count);

    private static InventoryLine Manual(params string[] numbers) => new(ProductId: "1", ToBinId: "1", SerialNumbers: numbers);
}
