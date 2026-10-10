namespace Wms.Domain.Common;

/// <summary>Strongly typed identifiers so a product ID can never be passed where a location ID is expected.</summary>
public readonly record struct WarehouseId(Guid Value)
{
    public static WarehouseId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct LocationId(Guid Value)
{
    public static LocationId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct InventoryTransactionId(Guid Value)
{
    public static InventoryTransactionId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}
