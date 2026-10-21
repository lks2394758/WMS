using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Inventory;

namespace Wms.Persistence.Abstractions;

/// <summary>Identifies one balance row: one product at one location.</summary>
public readonly record struct BalanceKey(WarehouseId WarehouseId, LocationId LocationId, ProductId ProductId);

/// <summary>A committed balance as stored, with the time of the transaction that last touched it.</summary>
public sealed record StoredBalance(BalanceKey Key, int AvailableQuantity, DateTimeOffset UpdatedAt);

public sealed record BalanceFilter(ProductId? ProductId = null, LocationId? LocationId = null);

public interface IInventoryReader
{
    /// <summary>Committed balances ordered by location ID, then product ID (the page key).</summary>
    Task<PageResult<StoredBalance>> ListBalancesAsync(BalanceFilter filter, PageRequest page, CancellationToken cancellationToken);
}

/// <summary>Opens the single write transaction of an inventory command.</summary>
public interface IInventoryStore
{
    Task<IInventoryWriteScope> BeginWriteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One inventory write transaction. Nothing is visible to other readers or writers until <see cref="CommitAsync"/>;
/// disposing without committing rolls everything back. A database provider must lock balance rows in a deterministic
/// order (location ID, then product ID) and read the idempotency record inside the same transaction.
/// </summary>
public interface IInventoryWriteScope : IAsyncDisposable
{
    /// <summary>The unexpired record for this user and key, if any.</summary>
    Task<IdempotencyRecord?> FindIdempotencyRecordAsync(
        UserId userId, Guid key, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The requested products that exist. Missing IDs are simply absent.</summary>
    Task<IReadOnlyDictionary<ProductId, Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> ids, CancellationToken cancellationToken);

    /// <summary>The requested locations that exist. Missing IDs are simply absent.</summary>
    Task<IReadOnlyDictionary<LocationId, Location>> GetLocationsAsync(
        IReadOnlyCollection<LocationId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Locks and loads the balances; a balance that does not exist yet is returned empty. Mutations go through
    /// <see cref="InventoryOperations"/> and are written back on commit.
    /// </summary>
    Task<IReadOnlyDictionary<BalanceKey, InventoryBalance>> LockBalancesAsync(
        IReadOnlyCollection<BalanceKey> keys, CancellationToken cancellationToken);

    /// <summary>Next number of the daily sequence for <paramref name="type"/>. Consumed only if the scope commits.</summary>
    Task<TransactionNumber> AllocateTransactionNumberAsync(
        InventoryTransactionType type, DateOnly businessDate, CancellationToken cancellationToken);

    void Add(InventoryTransaction transaction);

    void Add(AuditEntry entry);

    void Add(IdempotencyRecord record);

    /// <summary>Writes the locked balances (stamped <paramref name="committedAt"/>) and every added record atomically.</summary>
    Task CommitAsync(DateTimeOffset committedAt, CancellationToken cancellationToken);
}
