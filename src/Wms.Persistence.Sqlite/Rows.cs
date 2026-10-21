namespace Wms.Persistence.Sqlite;

// Persistence rows: the stored shape of the domain types. They never leave this assembly; the store maps them to
// detached domain objects. Ledger and audit rows are init-only (append-only, also enforced by triggers).

internal sealed class ProductRow
{
    public Guid Id { get; init; }
    public string Sku { get; init; } = "";

    /// <summary>Upper-invariant SKU: carries the case-insensitive unique index.</summary>
    public string NormalizedSku { get; init; } = "";

    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public int? Capacity { get; set; }
    public bool IsActive { get; set; }
}

internal sealed class LocationRow
{
    public Guid Id { get; init; }
    public Guid WarehouseId { get; init; }
    public string Code { get; init; } = "";
    public bool IsActive { get; set; }
}

internal sealed class BarcodeRow
{
    public string Value { get; init; } = "";
    public Guid ProductId { get; init; }
    public bool IsActive { get; set; }
}

internal sealed class BalanceRow
{
    public Guid WarehouseId { get; init; }
    public Guid LocationId { get; init; }
    public Guid ProductId { get; init; }

    /// <summary>Page key <c>{locationId:N}:{productId:N}</c>, the same ordinal order as the in-memory provider.</summary>
    public string PageKey { get; init; } = "";

    /// <summary>Written only from an <see cref="Domain.Inventory.InventoryBalance"/> changed by InventoryOperations.</summary>
    public int AvailableQuantity { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Application-managed optimistic concurrency token, incremented on every write.</summary>
    public long Version { get; set; }

    public static string PageKeyOf(Guid locationId, Guid productId) => $"{locationId:N}:{productId:N}";
}

internal sealed class InventoryTransactionRow
{
    public Guid Id { get; init; }
    public string Number { get; init; } = "";
    public int Type { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? Reason { get; init; }
    public string? ReferenceNo { get; init; }
    public List<InventoryTransactionLineRow> Lines { get; init; } = [];
}

internal sealed class InventoryTransactionLineRow
{
    public Guid TransactionId { get; init; }
    public int LineNo { get; init; }
    public Guid ProductId { get; init; }
    public Guid? FromLocationId { get; init; }
    public Guid? ToLocationId { get; init; }
    public int Quantity { get; init; }
    public int? FromBefore { get; init; }
    public int? FromAfter { get; init; }
    public int? ToBefore { get; init; }
    public int? ToAfter { get; init; }
}

internal sealed class TransactionSequenceRow
{
    public int Type { get; init; }
    public DateOnly BusinessDate { get; init; }
    public int LastValue { get; set; }
}

internal sealed class AuditEntryRow
{
    public Guid Id { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? ActorId { get; init; }
    public string Action { get; init; } = "";
    public string EntityType { get; init; } = "";
    public string? EntityId { get; init; }
    public string? Details { get; init; }
}

internal sealed class IdempotencyRow
{
    public Guid UserId { get; init; }
    public Guid Key { get; init; }
    public string Operation { get; init; } = "";
    public string RequestHash { get; init; } = "";
    public string ResponseJson { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class UserRow
{
    public Guid Id { get; init; }
    public string Username { get; init; } = "";

    /// <summary>Upper-invariant username: carries the case-insensitive unique index.</summary>
    public string NormalizedUsername { get; init; } = "";

    public string DisplayName { get; set; } = "";
    public int Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; }
}

internal sealed class RefreshTokenRow
{
    public Guid Id { get; init; }
    public Guid FamilyId { get; init; }
    public Guid UserId { get; init; }
    public string TokenHash { get; init; } = "";
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}
