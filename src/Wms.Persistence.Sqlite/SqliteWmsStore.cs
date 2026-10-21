using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite;

/// <summary>
/// SQLite demonstration provider for every persistence port. Reads use a short-lived context each. Writes are
/// serialized: first by an in-process queue (no busy polling between requests of this process), then by SQLite's
/// single writer lock, taken up front with <c>BEGIN IMMEDIATE</c> so a transaction never fails half-way on a lock it
/// could not upgrade. A write that cannot get both within <see cref="SqliteOptions.LockTimeoutSeconds"/> is rejected
/// with <see cref="PersistenceConflictException"/> and nothing is stored. This shows single-file, single-writer
/// behaviour only; it says nothing about SQL Server or PostgreSQL row locking (AGENTS.md rule 4).
/// </summary>
internal sealed class SqliteWmsStore(IDbContextFactory<WmsDbContext> contexts, SqliteOptions options)
    : ICatalogReader, IInventoryReader, IInventoryStore, IUserStore, IRefreshTokenStore, IAuditLog, ISeedStore, IDisposable
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly SemaphoreSlim _writer = new(1, 1);

    private TimeSpan LockTimeout => TimeSpan.FromSeconds(options.LockTimeoutSeconds);

    // ---- ICatalogReader ----

    public async Task<Product?> FindProductAsync(ProductId id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id.Value, cancellationToken);
        return row is null ? null : RowMapping.ToDomain(row);
    }

    public async Task<PageResult<Product>> ListProductsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Products.AsNoTracking();
        if (page.After is { } after)
        {
            query = query.Where(p => string.Compare(p.Sku, after) > 0);
        }

        var rows = await query.OrderBy(p => p.Sku).Take(page.Limit + 1).ToListAsync(cancellationToken);
        return ToPage(rows, page.Limit, p => p.Sku, RowMapping.ToDomain);
    }

    public async Task<Location?> FindLocationAsync(LocationId id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id.Value, cancellationToken);
        return row is null ? null : RowMapping.ToDomain(row);
    }

    public async Task<PageResult<Location>> ListLocationsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Locations.AsNoTracking();
        if (page.After is { } after)
        {
            query = query.Where(l => string.Compare(l.Code, after) > 0);
        }

        var rows = await query.OrderBy(l => l.Code).Take(page.Limit + 1).ToListAsync(cancellationToken);
        return ToPage(rows, page.Limit, l => l.Code, RowMapping.ToDomain);
    }

    public async Task<IReadOnlyDictionary<ProductId, IReadOnlyList<string>>> GetActiveBarcodesAsync(
        IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        var ids = productIds.Select(p => p.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<ProductId, IReadOnlyList<string>>();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Barcodes.AsNoTracking()
            .Where(b => b.IsActive && ids.Contains(b.ProductId))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(b => new ProductId(b.ProductId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(b => b.Value).Order(StringComparer.Ordinal).ToList());
    }

    public async Task<IReadOnlyList<Barcode>> FindActiveBarcodesAsync(string value, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Barcodes.AsNoTracking()
            .Where(b => b.Value == value && b.IsActive)
            .ToListAsync(cancellationToken);
        return rows.Select(RowMapping.ToDomain).ToList();
    }

    // ---- IInventoryReader ----

    public async Task<PageResult<StoredBalance>> ListBalancesAsync(
        BalanceFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Balances.AsNoTracking();
        if (filter.ProductId is { } productId)
        {
            query = query.Where(b => b.ProductId == productId.Value);
        }

        if (filter.LocationId is { } locationId)
        {
            query = query.Where(b => b.LocationId == locationId.Value);
        }

        if (page.After is { } after)
        {
            query = query.Where(b => string.Compare(b.PageKey, after) > 0);
        }

        var rows = await query.OrderBy(b => b.PageKey).Take(page.Limit + 1).ToListAsync(cancellationToken);
        return ToPage(rows, page.Limit, b => b.PageKey, RowMapping.ToStored);
    }

    // ---- IInventoryStore ----

    public async Task<IInventoryWriteScope> BeginWriteAsync(CancellationToken cancellationToken)
    {
        var (db, transaction) = await AcquireWriterAsync(cancellationToken);
        return new SqliteWriteScope(db, transaction, ReleaseWriter);
    }

    // ---- IUserStore ----

    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        var normalized = RowMapping.Normalize(username);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, cancellationToken);
        return row is null ? null : RowMapping.ToDomain(row);
    }

    public async Task<UserAccount?> FindByIdAsync(UserId id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id.Value, cancellationToken);
        return row is null ? null : RowMapping.ToDomain(row);
    }

    // ---- IRefreshTokenStore ----

    public Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return WriteAsync(
            async db =>
            {
                db.RefreshTokens.Add(RowMapping.ToRow(record));
                return await db.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);
    }

    public async Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.RefreshTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : RowMapping.ToDomain(row);
    }

    public Task<bool> TryRotateAsync(
        Guid currentId, RefreshTokenRecord replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return WriteAsync(
            async db =>
            {
                // Conditional update inside the write transaction: only one caller can revoke an unrevoked token.
                var revoked = await db.RefreshTokens
                    .Where(t => t.Id == currentId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, replacement.Id),
                        cancellationToken);
                if (revoked == 0)
                {
                    return false;
                }

                db.RefreshTokens.Add(RowMapping.ToRow(replacement));
                await db.SaveChangesAsync(cancellationToken);
                return true;
            },
            cancellationToken);
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        WriteAsync(
            db => db.RefreshTokens
                .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken),
            cancellationToken);

    // ---- IAuditLog ----

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return WriteAsync(
            async db =>
            {
                db.AuditEntries.Add(RowMapping.ToRow(entry));
                return await db.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);
    }

    // ---- ISeedStore ----

    public Task<int> AddMissingAsync(SeedData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        return WriteAsync(
            async db =>
            {
                var locationIds = data.Locations.Select(l => l.Id.Value).ToList();
                var locationCodes = data.Locations.Select(l => l.Code).ToList();
                var existingLocations = await db.Locations.AsNoTracking()
                    .Where(l => locationIds.Contains(l.Id) || locationCodes.Contains(l.Code))
                    .Select(l => new { l.Id, l.WarehouseId, l.Code })
                    .ToListAsync(cancellationToken);
                var newLocations = data.Locations.Where(l => !existingLocations.Any(x =>
                    x.Id == l.Id.Value || (x.WarehouseId == l.WarehouseId.Value && x.Code == l.Code))).ToList();

                var productIds = data.Products.Select(p => p.Id.Value).ToList();
                var skus = data.Products.Select(p => RowMapping.Normalize(p.Sku)).ToList();
                var existingProducts = await db.Products.AsNoTracking()
                    .Where(p => productIds.Contains(p.Id) || skus.Contains(p.NormalizedSku))
                    .Select(p => new { p.Id, p.NormalizedSku })
                    .ToListAsync(cancellationToken);
                var newProducts = data.Products.Where(p => !existingProducts.Any(x =>
                    x.Id == p.Id.Value || x.NormalizedSku == RowMapping.Normalize(p.Sku))).ToList();

                var values = data.Barcodes.Select(b => b.Value).ToList();
                var existingBarcodes = await db.Barcodes.AsNoTracking()
                    .Where(b => values.Contains(b.Value))
                    .Select(b => b.Value)
                    .ToListAsync(cancellationToken);
                var newBarcodes = data.Barcodes.Where(b => !existingBarcodes.Contains(b.Value, StringComparer.Ordinal)).ToList();

                var userIds = data.Users.Select(u => u.Id.Value).ToList();
                var usernames = data.Users.Select(u => RowMapping.Normalize(u.Username)).ToList();
                var existingUsers = await db.Users.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id) || usernames.Contains(u.NormalizedUsername))
                    .Select(u => new { u.Id, u.NormalizedUsername })
                    .ToListAsync(cancellationToken);
                var newUsers = data.Users.Where(u => !existingUsers.Any(x =>
                    x.Id == u.Id.Value || x.NormalizedUsername == RowMapping.Normalize(u.Username))).ToList();

                db.Locations.AddRange(newLocations.Select(RowMapping.ToRow));
                db.Products.AddRange(newProducts.Select(RowMapping.ToRow));
                db.Barcodes.AddRange(newBarcodes.Select(RowMapping.ToRow));
                db.Users.AddRange(newUsers.Select(RowMapping.ToRow));
                await db.SaveChangesAsync(cancellationToken);
                return newLocations.Count + newProducts.Count + newBarcodes.Count + newUsers.Count;
            },
            cancellationToken);
    }

    public Task<int> ReplacePasswordHashAsync(
        IReadOnlyCollection<UserId> userIds, string expectedHash, string newHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var ids = userIds.Select(u => u.Value).ToList();
        return WriteAsync(
            db => db.Users
                .Where(u => ids.Contains(u.Id) && u.PasswordHash == expectedHash)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, newHash), cancellationToken),
            cancellationToken);
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>True for SQLITE_BUSY / SQLITE_LOCKED anywhere in the exception chain.</summary>
    internal static bool IsLockConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One short write transaction for the auth, audit and seed ports; committed if <paramref name="work"/> returns.</summary>
    private async Task<T> WriteAsync<T>(Func<WmsDbContext, Task<T>> work, CancellationToken cancellationToken)
    {
        var (db, transaction) = await AcquireWriterAsync(cancellationToken);
        try
        {
            var result = await work(db);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception e) when (IsLockConflict(e))
        {
            throw new PersistenceConflictException("The SQLite database stayed locked by another writer.", e);
        }
        finally
        {
            await transaction.DisposeAsync();
            await db.DisposeAsync();
            ReleaseWriter();
        }
    }

    /// <summary>Takes the in-process writer slot, then the database writer lock (<c>BEGIN IMMEDIATE</c>).</summary>
    /// <remarks>The caller owns both: dispose the transaction (rolls back unless committed), then the context.</remarks>
    private async Task<(WmsDbContext Db, SqliteTransaction Transaction)> AcquireWriterAsync(CancellationToken cancellationToken)
    {
        if (!await _writer.WaitAsync(LockTimeout, cancellationToken))
        {
            throw new PersistenceConflictException(
                $"No write slot became free within {options.LockTimeoutSeconds} s; nothing was committed.");
        }

        WmsDbContext? db = null;
        try
        {
            db = await contexts.CreateDbContextAsync(cancellationToken);
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: waits up to DefaultTimeout
            try
            {
                await db.Database.UseTransactionAsync(transaction, cancellationToken); // EF enlists, the caller owns it
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }

            return (db, transaction);
        }
        catch (Exception e)
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }

            ReleaseWriter();
            if (IsLockConflict(e))
            {
                throw new PersistenceConflictException(
                    $"The SQLite database stayed locked by another process for {options.LockTimeoutSeconds} s; nothing was committed.", e);
            }

            throw;
        }
    }

    private void ReleaseWriter() => _writer.Release();

    private static PageResult<TOut> ToPage<TRow, TOut>(
        List<TRow> rows, int limit, Func<TRow, string> keyOf, Func<TRow, TOut> map)
    {
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PageResult<TOut>(rows.Select(map).ToList(), hasMore ? keyOf(rows[^1]) : null);
    }
}
