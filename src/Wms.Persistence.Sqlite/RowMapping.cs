using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Identity;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite;

/// <summary>Row ↔ domain mapping. Domain objects are rebuilt through their factories, so stored data is re-validated.</summary>
internal static class RowMapping
{
    public static string Normalize(string value) => value.ToUpperInvariant();

    public static Product ToDomain(ProductRow row)
    {
        var product = Product.Create(new ProductId(row.Id), row.Sku, row.Name, row.Unit, row.Capacity).Value;
        if (!row.IsActive)
        {
            product.Deactivate();
        }

        return product;
    }

    public static ProductRow ToRow(Product product) => new()
    {
        Id = product.Id.Value,
        Sku = product.Sku,
        NormalizedSku = Normalize(product.Sku),
        Name = product.Name,
        Unit = product.Unit,
        Capacity = product.Capacity,
        IsActive = product.IsActive,
    };

    public static Location ToDomain(LocationRow row)
    {
        var location = Location.Create(new LocationId(row.Id), new WarehouseId(row.WarehouseId), row.Code).Value;
        if (!row.IsActive)
        {
            location.Deactivate();
        }

        return location;
    }

    public static LocationRow ToRow(Location location) => new()
    {
        Id = location.Id.Value,
        WarehouseId = location.WarehouseId.Value,
        Code = location.Code,
        IsActive = location.IsActive,
    };

    public static Barcode ToDomain(BarcodeRow row)
    {
        var barcode = Barcode.Create(row.Value, new ProductId(row.ProductId)).Value;
        if (!row.IsActive)
        {
            barcode.Deactivate();
        }

        return barcode;
    }

    public static BarcodeRow ToRow(Barcode barcode) => new()
    {
        Value = barcode.Value,
        ProductId = barcode.ProductId.Value,
        IsActive = barcode.IsActive,
    };

    public static StoredBalance ToStored(BalanceRow row) => new(
        new BalanceKey(new WarehouseId(row.WarehouseId), new LocationId(row.LocationId), new ProductId(row.ProductId)),
        row.AvailableQuantity,
        row.UpdatedAt);

    public static UserAccount ToDomain(UserRow row) => new(
        new UserId(row.Id), row.Username, row.DisplayName, (UserRole)row.Role, row.PasswordHash, row.IsActive);

    public static UserRow ToRow(UserAccount user) => new()
    {
        Id = user.Id.Value,
        Username = user.Username,
        NormalizedUsername = Normalize(user.Username),
        DisplayName = user.DisplayName,
        Role = (int)user.Role,
        PasswordHash = user.PasswordHash,
        IsActive = user.IsActive,
    };

    public static RefreshTokenRecord ToDomain(RefreshTokenRow row) => new(
        row.Id, row.FamilyId, new UserId(row.UserId), row.TokenHash, row.IssuedAt, row.ExpiresAt, row.RevokedAt, row.ReplacedById);

    public static RefreshTokenRow ToRow(RefreshTokenRecord record) => new()
    {
        Id = record.Id,
        FamilyId = record.FamilyId,
        UserId = record.UserId.Value,
        TokenHash = record.TokenHash,
        IssuedAt = record.IssuedAt,
        ExpiresAt = record.ExpiresAt,
        RevokedAt = record.RevokedAt,
        ReplacedById = record.ReplacedById,
    };

    public static AuditEntryRow ToRow(AuditEntry entry) => new()
    {
        Id = entry.Id,
        OccurredAt = entry.OccurredAt,
        ActorId = entry.ActorId?.Value,
        Action = entry.Action,
        EntityType = entry.EntityType,
        EntityId = entry.EntityId,
        Details = entry.Details,
    };

    public static IdempotencyRecord ToDomain(IdempotencyRow row) => new(
        new UserId(row.UserId), row.Key, row.Operation, row.RequestHash, row.ResponseJson, row.CreatedAt, row.ExpiresAt);

    public static IdempotencyRow ToRow(IdempotencyRecord record) => new()
    {
        UserId = record.UserId.Value,
        Key = record.Key,
        Operation = record.Operation,
        RequestHash = record.RequestHash,
        ResponseJson = record.ResponseJson,
        CreatedAt = record.CreatedAt,
        ExpiresAt = record.ExpiresAt,
    };

    public static InventoryTransactionRow ToRow(InventoryTransaction transaction) => new()
    {
        Id = transaction.Id.Value,
        Number = transaction.Number.Value,
        Type = (int)transaction.Type,
        ActorId = transaction.ActorId.Value,
        OccurredAt = transaction.OccurredAt,
        Reason = transaction.Reason,
        ReferenceNo = transaction.ReferenceNo,
        Lines = transaction.Lines.Select(line => new InventoryTransactionLineRow
        {
            TransactionId = transaction.Id.Value,
            LineNo = line.LineNo,
            ProductId = line.ProductId.Value,
            FromLocationId = line.FromLocationId?.Value,
            ToLocationId = line.ToLocationId?.Value,
            Quantity = line.Quantity,
            FromBefore = line.From?.Before,
            FromAfter = line.From?.After,
            ToBefore = line.To?.Before,
            ToAfter = line.To?.After,
        }).ToList(),
    };
}
