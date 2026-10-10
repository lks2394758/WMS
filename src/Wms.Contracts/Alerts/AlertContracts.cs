namespace Wms.Contracts.Alerts;

public sealed record AlertResponse(
    Guid Id,
    Guid WarehouseId,
    Guid ProductId,
    string Type,
    string Status,
    int AvailableQuantity,
    int Capacity,
    DateTimeOffset OpenedAt,
    DateTimeOffset? AcknowledgedAt = null,
    Guid? AcknowledgedBy = null,
    DateTimeOffset? ClosedAt = null);

public static class AlertTypes
{
    public const string LowStock = "lowStock";
    public const string Overstock = "overstock";
}

public static class AlertStatuses
{
    public const string Opened = "opened";
    public const string Acknowledged = "acknowledged";
    public const string Closed = "closed";
}
