using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite;

/// <summary>
/// One inventory command in one SQLite transaction opened with <c>BEGIN IMMEDIATE</c>: from the first read the
/// scope holds the database's single writer lock, so the idempotency record, balances and sequence it reads cannot
/// change before it commits. Balances, ledger, audit entry, idempotency record and sequence are written by one
/// <c>SaveChanges</c> and one <c>COMMIT</c>; disposing without committing rolls everything back.
/// </summary>
internal sealed class SqliteWriteScope(WmsDbContext db, SqliteTransaction transaction, Action releaseWriter)
    : IInventoryWriteScope
{
    private readonly Dictionary<BalanceKey, LockedBalance> _locked = new();
    private readonly Dictionary<(InventoryTransactionType Type, DateOnly Date), TransactionSequenceRow> _sequences = new();
    private readonly List<InventoryTransaction> _transactions = [];
    private readonly List<AuditEntry> _audit = [];
    private readonly List<IdempotencyRecord> _idempotency = [];
    private bool _completed;
    private bool _disposed;

    public async Task<IdempotencyRecord?> FindIdempotencyRecordAsync(
        UserId userId, Guid key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        var row = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId.Value && r.Key == key, cancellationToken);
        return row is not null && row.ExpiresAt > now ? RowMapping.ToDomain(row) : null;
    }

    public async Task<IReadOnlyDictionary<ProductId, Product>> GetProductsAsync(
        IReadOnlyCollection<ProductId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfCompleted();
        var wanted = ids.Select(id => id.Value).Distinct().ToList();
        var rows = await db.Products.AsNoTracking().Where(p => wanted.Contains(p.Id)).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => new ProductId(r.Id), RowMapping.ToDomain);
    }

    public async Task<IReadOnlyDictionary<LocationId, Location>> GetLocationsAsync(
        IReadOnlyCollection<LocationId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfCompleted();
        var wanted = ids.Select(id => id.Value).Distinct().ToList();
        var rows = await db.Locations.AsNoTracking().Where(l => wanted.Contains(l.Id)).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => new LocationId(r.Id), RowMapping.ToDomain);
    }

    public async Task<IReadOnlyDictionary<BalanceKey, InventoryBalance>> LockBalancesAsync(
        IReadOnlyCollection<BalanceKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfCompleted();

        // Deterministic order (location ID, then product ID). SQLite locks the whole database for this transaction
        // already; the order matters for providers with row locks and keeps the behaviour identical.
        var missing = keys.Distinct()
            .Where(k => !_locked.ContainsKey(k))
            .OrderBy(k => BalanceRow.PageKeyOf(k.LocationId.Value, k.ProductId.Value), StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            var locationIds = missing.Select(k => k.LocationId.Value).Distinct().ToList();
            var productIds = missing.Select(k => k.ProductId.Value).Distinct().ToList();
            var rows = await db.Balances // tracked: written back on commit with the original Version as token
                .Where(b => locationIds.Contains(b.LocationId) && productIds.Contains(b.ProductId))
                .OrderBy(b => b.PageKey)
                .ToListAsync(cancellationToken);
            var stored = rows.ToDictionary(r => new BalanceKey(
                new WarehouseId(r.WarehouseId), new LocationId(r.LocationId), new ProductId(r.ProductId)));

            foreach (var key in missing)
            {
                _locked[key] = stored.TryGetValue(key, out var row)
                    ? new LockedBalance(row, InventoryBalance.Restore(key.WarehouseId, key.LocationId, key.ProductId, row.AvailableQuantity))
                    : new LockedBalance(null, InventoryBalance.Empty(key.WarehouseId, key.LocationId, key.ProductId));
            }
        }

        IReadOnlyDictionary<BalanceKey, InventoryBalance> result = keys.Distinct().ToDictionary(k => k, k => _locked[k].Balance);
        return result;
    }

    public async Task<TransactionNumber> AllocateTransactionNumberAsync(
        InventoryTransactionType type, DateOnly businessDate, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        var slot = (type, businessDate);
        if (!_sequences.TryGetValue(slot, out var row))
        {
            var typeValue = (int)type;
            row = await db.TransactionSequences
                .SingleOrDefaultAsync(s => s.Type == typeValue && s.BusinessDate == businessDate, cancellationToken);
            if (row is null)
            {
                row = new TransactionSequenceRow { Type = typeValue, BusinessDate = businessDate, LastValue = 0 };
                db.TransactionSequences.Add(row);
            }

            _sequences[slot] = row;
        }

        var next = row.LastValue + 1;
        var number = TransactionNumber.Create(type, businessDate, next); // throws past the daily maximum
        row.LastValue = next; // consumed only if the scope commits
        return number;
    }

    public void Add(InventoryTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ThrowIfCompleted();
        _transactions.Add(transaction);
    }

    public void Add(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfCompleted();
        _audit.Add(entry);
    }

    public void Add(IdempotencyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ThrowIfCompleted();
        _idempotency.Add(record);
    }

    public async Task CommitAsync(DateTimeOffset committedAt, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        _completed = true; // a failed commit leaves the scope unusable; dispose rolls it back

        try
        {
            foreach (var (key, locked) in _locked)
            {
                if (locked.Row is { } row)
                {
                    row.AvailableQuantity = locked.Balance.AvailableQuantity;
                    row.UpdatedAt = committedAt;
                    row.Version++; // UPDATE ... WHERE Version = <original>: a concurrent change makes it affect 0 rows
                }
                else
                {
                    db.Balances.Add(new BalanceRow
                    {
                        WarehouseId = key.WarehouseId.Value,
                        LocationId = key.LocationId.Value,
                        ProductId = key.ProductId.Value,
                        PageKey = BalanceRow.PageKeyOf(key.LocationId.Value, key.ProductId.Value),
                        AvailableQuantity = locked.Balance.AvailableQuantity,
                        UpdatedAt = committedAt,
                        Version = 1,
                    });
                }
            }

            // Expired keys go first, so an expired record with the same (user, key) never blocks the new one.
            await db.IdempotencyRecords.Where(r => r.ExpiresAt <= committedAt).ExecuteDeleteAsync(cancellationToken);

            db.InventoryTransactions.AddRange(_transactions.Select(RowMapping.ToRow));
            db.AuditEntries.AddRange(_audit.Select(RowMapping.ToRow));
            db.IdempotencyRecords.AddRange(_idempotency.Select(RowMapping.ToRow));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new PersistenceConflictException("A balance changed underneath this transaction; nothing was committed.", e);
        }
        catch (Exception e) when (SqliteWmsStore.IsLockConflict(e))
        {
            throw new PersistenceConflictException("The SQLite database stayed locked; nothing was committed.", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _completed = true;
        try
        {
            await transaction.DisposeAsync(); // rolls back unless committed
            await db.DisposeAsync();
        }
        finally
        {
            releaseWriter();
        }
    }

    private void ThrowIfCompleted() => ObjectDisposedException.ThrowIf(_completed, this);

    private sealed record LockedBalance(BalanceRow? Row, InventoryBalance Balance);
}
