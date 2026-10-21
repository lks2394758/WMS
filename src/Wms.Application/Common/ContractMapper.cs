using Wms.Contracts.Catalog;
using Wms.Contracts.Inventory;
using Wms.Domain.Catalog;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Common;

/// <summary>Domain and persistence types to transport DTOs. The only place that knows both sides.</summary>
internal static class ContractMapper
{
    public static ProductResponse ToResponse(Product product, IReadOnlyList<string> barcodes) =>
        new(product.Id.Value, product.Sku, product.Name, product.Unit, product.Capacity, product.IsActive, barcodes);

    public static LocationResponse ToResponse(Location location) =>
        new(location.Id.Value, location.WarehouseId.Value, location.Code, location.IsActive);

    public static InventoryBalanceResponse ToResponse(StoredBalance balance) =>
        new(
            balance.Key.WarehouseId.Value,
            balance.Key.LocationId.Value,
            balance.Key.ProductId.Value,
            balance.AvailableQuantity,
            balance.UpdatedAt);

    public static InventoryBalanceResponse ToResponse(InventoryBalance balance, DateTimeOffset updatedAt) =>
        new(balance.WarehouseId.Value, balance.LocationId.Value, balance.ProductId.Value, balance.AvailableQuantity, updatedAt);

    public static InventoryTransactionResponse ToResponse(
        InventoryTransaction transaction, IReadOnlyList<InventoryBalanceResponse> balances) =>
        new(
            transaction.Number.Value,
            WireNames.Of(transaction.Type),
            transaction.OccurredAt,
            transaction.ActorId.Value,
            transaction.Lines.Select(ToResponse).ToList(),
            balances,
            transaction.Reason,
            transaction.ReferenceNo);

    private static TransactionLine ToResponse(InventoryTransactionLine line) =>
        new(
            line.LineNo,
            line.ProductId.Value,
            line.Quantity,
            line is { FromLocationId: { } fromId, From: { } fromChange }
                ? new BalanceSnapshot(fromId.Value, fromChange.Before, fromChange.After)
                : null,
            line is { ToLocationId: { } toId, To: { } toChange }
                ? new BalanceSnapshot(toId.Value, toChange.Before, toChange.After)
                : null);
}
