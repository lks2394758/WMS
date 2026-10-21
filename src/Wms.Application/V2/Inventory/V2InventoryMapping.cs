using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Inventory;

/// <summary>Stored ledger records to v2 wire contracts (string IDs, canonical exact quantity strings).</summary>
public static class V2InventoryMapping
{
    public static InventoryTransactionResponse ToResponse(V2TransactionRecord t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new InventoryTransactionResponse(
            WireId.Format(t.Id),
            t.Number,
            t.Type,
            WireId.Format(t.ActorId),
            t.ActorName,
            t.ActorRole,
            t.OccurredAt,
            t.Lines.Select(ToLine).ToList(),
            t.Source,
            t.Destination,
            t.Reason,
            t.CountTaskId is { } task ? WireId.Format(task) : null,
            t.CountResultId is { } result ? WireId.Format(result) : null);
    }

    public static TransactionListItem ToListItem(V2TransactionSummary t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new TransactionListItem(
            WireId.Format(t.Id), t.Number, t.Type, WireId.Format(t.ActorId), t.ActorName, t.ActorRole, t.OccurredAt, t.LineCount,
            t.Source, t.Destination, t.Reason, t.CountResultId is { } result ? WireId.Format(result) : null);
    }

    private static TransactionLineDetail ToLine(V2TransactionLineRecord l) => new(
        l.LineNo,
        WireId.Format(l.ProductId),
        l.ProductName,
        l.Sku,
        l.Barcode,
        l.UnitName,
        l.DecimalPlaces,
        WireQuantity.Format(l.QuantityMicros),
        Id(l.FromBinId),
        l.FromBinCode,
        Quantity(l.FromBeforeMicros),
        Quantity(l.FromAfterMicros),
        Id(l.ToBinId),
        l.ToBinCode,
        Quantity(l.ToBeforeMicros),
        Quantity(l.ToAfterMicros),
        l.PackagingUnit,
        Quantity(l.PackagingQuantityMicros),
        Quantity(l.ConversionFactorMicros),
        l.Items.Count == 0 ? null : l.Items.Select(i => new LineSerialItem(WireId.Format(i.SerializedItemId), i.SerialNumber)).ToList());

    private static string? Id(long? id) => id is { } value ? WireId.Format(value) : null;

    private static string? Quantity(long? micros) => micros is { } value ? WireQuantity.Format(value) : null;
}
