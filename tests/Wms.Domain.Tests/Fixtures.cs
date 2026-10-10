using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Inventory;

namespace Wms.Domain.Tests;

internal static class Fixtures
{
    public static readonly WarehouseId Warehouse = WarehouseId.New();
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);

    public static Product NewProduct(int? capacity = 100) =>
        Product.Create(ProductId.New(), "SKU-001", "Widget", "EA", capacity).Value;

    public static Location NewLocation(string code = "A-01-01") =>
        Location.Create(LocationId.New(), Warehouse, code).Value;

    public static InventoryBalance BalanceOf(Product product, Location location, int available) =>
        InventoryBalance.Restore(Warehouse, location.Id, product.Id, available);

    public static Quantity Qty(int value) => Quantity.ForTransaction(value).Value;

    public static InventoryTransactionContext Context(InventoryTransactionType type, string? reason = null) =>
        new(InventoryTransactionId.New(), TransactionNumber.Create(type, new DateOnly(2026, 10, 10), 1), UserId.New(), Now, reason);

    public static InventoryTransactionLine AnyLine(int quantity = 1) =>
        new(1, ProductId.New(), null, LocationId.New(), quantity, null, new BalanceChange(0, quantity));
}
