namespace Wms.Contracts.Events;

/// <summary>
/// SignalR notification sent after an inventory transaction commits (guide §10.3). It is a hint to refresh,
/// never the system of record: delivery is at-least-once, so de-duplicate by <see cref="EventId"/>.
/// </summary>
public sealed record InventoryChangedV1(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid WarehouseId,
    string TransactionNo,
    Guid ProductId,
    IReadOnlyList<AffectedBalance> AffectedBalances,
    IReadOnlyList<AlertChange> AlertChanges,
    string EventType = InventoryChangedV1.Name)
{
    public const string Name = "inventory.changed.v1";

    /// <summary>Client method name invoked on the hub connection.</summary>
    public const string HubMethod = "InventoryChanged";
}

public sealed record AffectedBalance(Guid LocationId, int AvailableQuantity);

public sealed record AlertChange(string Type, string Status);
