using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 catalog ports on SQLite: reads use short-lived contexts; writes use <see cref="V2SqliteCatalogScope"/>.</summary>
internal sealed partial class V2SqliteStore : IV2CatalogReader, IV2CatalogStore
{
    // Lists (not arrays) so LINQ binds List.Contains, which EF Core translates to SQL IN.
    internal static readonly List<string> CapacityAlertTypes = ["lowStock", "highStock", "overCapacity"];
    internal static readonly List<string> OpenCountStatuses = ["inProgress", "pendingConfirmation"];

    // ---- IV2CatalogReader ----

    public async Task<IReadOnlyList<V2UnitRecord>> ListUnitsAsync(string? normalizedSearch, long? afterId, int limit, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Units.AsNoTracking();
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            query = query.Where(u => u.NormalizedName.Contains(normalizedSearch));
        }

        if (afterId is { } after)
        {
            query = query.Where(u => u.Id > after);
        }

        return await query.OrderBy(u => u.Id).Take(limit)
            .Select(u => new V2UnitRecord(u.Id, u.Name, u.IsSystem, u.Version, db.Products.Count(p => p.UnitId == u.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<V2UnitRecord?> FindUnitAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.FindUnitAsync(db, id, cancellationToken);
    }

    public async Task<IReadOnlyList<V2CategoryRecord>> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.ListCategoriesAsync(db, cancellationToken);
    }

    public async Task<IReadOnlyList<V2ProductListRow>> ListProductsAsync(V2ProductFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Products.AsNoTracking().Where(p => !p.IsArchived);
        if (filter.CategoryIds is { } categories)
        {
            var ids = categories.ToList();
            query = query.Where(p => db.ProductCategories.Any(pc => pc.ProductId == p.Id && ids.Contains(pc.CategoryId)));
        }

        if (filter.AfterId is { } after)
        {
            query = query.Where(p => p.Id > after);
        }

        var ordered = query.OrderBy(p => p.Id);
        List<V2SqliteCatalogScope.ProductProjection> rows;
        if (string.IsNullOrWhiteSpace(filter.Search))
        {
            rows = await V2SqliteCatalogScope.ProjectProducts(db, ordered.Take(filter.Limit)).ToListAsync(cancellationToken);
        }
        else
        {
            // SQLite's upper()/LIKE/NOCASE fold ASCII only, so the name is matched here with the same invariant upper case
            // the normalized SKU/barcode columns use. Candidates stream in ID order and the page is cut only after the
            // match, so paging stays exact.
            var search = filter.Search.Trim().ToUpperInvariant();
            var matched = new List<long>(filter.Limit);
            var candidates = ordered.Select(p => new { p.Id, p.Name, p.NormalizedSku, p.NormalizedBarcode }).AsAsyncEnumerable();
            await foreach (var candidate in candidates.WithCancellation(cancellationToken))
            {
                if (candidate.Name.ToUpperInvariant().Contains(search, StringComparison.Ordinal)
                    || candidate.NormalizedSku.Contains(search, StringComparison.Ordinal)
                    || candidate.NormalizedBarcode.Contains(search, StringComparison.Ordinal))
                {
                    matched.Add(candidate.Id);
                    if (matched.Count == filter.Limit)
                    {
                        break;
                    }
                }
            }

            rows = await V2SqliteCatalogScope.ProjectProducts(db, db.Products.AsNoTracking().Where(p => matched.Contains(p.Id)))
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken);
        }
        var productIds = rows.Select(r => r.Id).ToList();
        var links = await V2SqliteCatalogScope.CategoryLinksAsync(db, productIds, cancellationToken);
        var quantities = (await db.Bins.AsNoTracking()
                .Where(b => b.ProductId != null && productIds.Contains(b.ProductId.Value) && !b.IsArchived)
                .Select(b => new { ProductId = b.ProductId!.Value, b.QuantityMicros })
                .ToListAsync(cancellationToken))
            .ToLookup(b => b.ProductId, b => b.QuantityMicros);

        return rows
            .Select(r => new V2ProductListRow(V2SqliteCatalogScope.ToRecord(r, links), quantities[r.Id].ToList()))
            .ToList();
    }

    public async Task<V2ProductDetail?> FindProductAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.FindProductAsync(db, id, cancellationToken);
    }

    public async Task<V2ThresholdDefaults> GetThresholdDefaultsAsync(CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var settings = await db.SystemSettings.AsNoTracking().SingleAsync(s => s.Id == V2SystemSettingsRow.SingletonId, cancellationToken);
        return new V2ThresholdDefaults(settings.LowThresholdMicros, settings.HighThresholdMicros);
    }

    // ---- IV2CatalogStore ----

    public async Task<IV2CatalogWriteScope> BeginWriteAsync(CancellationToken cancellationToken)
    {
        if (!await _writer.WaitAsync(LockTimeout, cancellationToken))
        {
            throw new PersistenceConflictException("The v2 database is busy with another change; nothing was committed.");
        }

        WmsV2DbContext? db = null;
        try
        {
            db = await Contexts.CreateDbContextAsync(cancellationToken);
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            return new V2SqliteCatalogScope(db, transaction, () => _writer.Release());
        }
        catch (Exception e)
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }

            _writer.Release();
            if (IsConflict(e))
            {
                throw new PersistenceConflictException("The v2 database stayed locked; nothing was committed.", e);
            }

            throw;
        }
    }
}

/// <summary>
/// One catalog write transaction (<c>BEGIN IMMEDIATE</c>, writer queue held until dispose). Each change is saved at once
/// inside the transaction so later reads see it; nothing is visible to others until <see cref="CommitAsync"/>.
/// </summary>
internal sealed partial class V2SqliteCatalogScope(WmsV2DbContext db, IDbContextTransaction transaction, Action release) : IV2CatalogWriteScope
{
    private bool _disposed;

    /// <summary>The transaction's context, for the other parts of this class.</summary>
    private WmsV2DbContext Db => db;

    public async Task<V2AuthenticatedSession?> FindSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        var found = await db.LoginSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .SingleOrDefaultAsync(cancellationToken);
        return found is null ? null : new V2AuthenticatedSession(V2SqliteStore.ToRecord(found.Session), V2SqliteStore.ToRecord(found.User));
    }

    public Task<V2UnitRecord?> FindUnitAsync(long id, CancellationToken cancellationToken) => FindUnitAsync(db, id, cancellationToken);

    public async Task<V2UnitRecord?> FindUnitByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken)
    {
        var id = await db.Units.AsNoTracking().Where(u => u.NormalizedName == normalizedName).Select(u => (long?)u.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return id is null ? null : await FindUnitAsync(db, id.Value, cancellationToken);
    }

    public async Task<long> InsertUnitAsync(string name, string normalizedName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = new V2UnitRow { Name = name, NormalizedName = normalizedName, IsSystem = false, CreatedAt = now, UpdatedAt = now, Version = 1 };
        db.Units.Add(row);
        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public async Task<bool> UpdateUnitAsync(
        long id, int expectedVersion, string name, string normalizedName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.Units.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (row is null || row.Version != expectedVersion)
        {
            return false;
        }

        row.Name = name;
        row.NormalizedName = normalizedName;
        row.UpdatedAt = now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task DeleteUnitAsync(long id, CancellationToken cancellationToken) =>
        await Guard(() => db.Units.Where(u => u.Id == id).ExecuteDeleteAsync(cancellationToken));

    public Task<IReadOnlyList<V2CategoryRecord>> ListCategoriesAsync(CancellationToken cancellationToken) =>
        ListCategoriesAsync(db, cancellationToken);

    public async Task<long> InsertCategoryAsync(string name, string normalizedName, long? parentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = new V2CategoryRow
        {
            Name = name, NormalizedName = normalizedName, ParentId = parentId, CreatedAt = now, UpdatedAt = now, Version = 1,
        };
        db.Categories.Add(row);
        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public async Task<bool> UpdateCategoryAsync(
        long id, int expectedVersion, string name, string normalizedName, long? parentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (row is null || row.Version != expectedVersion)
        {
            return false;
        }

        row.Name = name;
        row.NormalizedName = normalizedName;
        row.ParentId = parentId;
        row.UpdatedAt = now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task DeleteCategoryAsync(long id, CancellationToken cancellationToken) =>
        await Guard(() => db.Categories.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken));

    public Task<V2ProductDetail?> FindProductAsync(long id, CancellationToken cancellationToken) => FindProductAsync(db, id, cancellationToken);

    public async Task<V2ProductRecord?> FindProductByCreationRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken)
    {
        var row = await ProjectProducts(db, db.Products.AsNoTracking()
                .Where(p => p.CreatedById == actorId && p.CreationRequestKey == requestKey))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return ToRecord(row, await CategoryLinksAsync(db, [row.Id], cancellationToken));
    }

    public async Task<IReadOnlyList<V2CodeOwner>> ListProductCodesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Products.AsNoTracking()
            .Select(p => new { p.Id, p.Sku, p.Barcode, p.HistoricalSkus, p.HistoricalBarcodes })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new V2CodeOwner(r.Id, r.Sku, r.Barcode, Strings(r.HistoricalSkus), Strings(r.HistoricalBarcodes))).ToList();
    }

    public async Task<long> NextNumberAsync(V2Counter counter, CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.SingleAsync(s => s.Id == V2SystemSettingsRow.SingletonId, cancellationToken);
        var next = counter switch
        {
            V2Counter.Sku => ++settings.LastSkuNumber,
            V2Counter.Barcode => ++settings.LastBarcodeNumber,
            V2Counter.SerialNumber => ++settings.LastSerialNumber,
            V2Counter.Transaction => ++settings.LastTransactionNumber,
            V2Counter.CountTask => ++settings.LastCountTaskNumber,
            _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, "Unknown counter."),
        };

        await SaveAsync(cancellationToken); // technical counter: the settings Version is not bumped
        return next;
    }

    public async Task<long> InsertProductAsync(V2NewProduct product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        var row = new V2ProductRow
        {
            Name = product.Name,
            Sku = product.Sku,
            NormalizedSku = product.NormalizedSku,
            Barcode = product.Barcode,
            NormalizedBarcode = product.NormalizedBarcode,
            HistoricalSkus = "[]",
            HistoricalBarcodes = "[]",
            UnitId = product.UnitId,
            DecimalPlaces = product.DecimalPlaces,
            SerialTracking = product.SerialTracking,
            Image = product.Image?.Content,
            ImageContentType = product.Image?.ContentType,
            IsArchived = false,
            CreatedById = product.CreatedById,
            CreationRequestKey = product.CreationRequestKey,
            CreationRequestDigest = product.CreationRequestDigest,
            CreatedAt = product.Now,
            UpdatedAt = product.Now,
            Version = 1,
        };
        db.Products.Add(row);
        await SaveAsync(cancellationToken);
        foreach (var categoryId in product.CategoryIds.Distinct())
        {
            db.ProductCategories.Add(new V2ProductCategoryRow { ProductId = row.Id, CategoryId = categoryId, CreatedAt = product.Now });
        }

        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public async Task<bool> UpdateProductAsync(V2ProductUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await db.Products.SingleOrDefaultAsync(p => p.Id == update.Id, cancellationToken);
        if (row is null || row.Version != update.ExpectedVersion)
        {
            return false;
        }

        row.Name = update.Name;
        row.Sku = update.Sku;
        row.NormalizedSku = update.NormalizedSku;
        row.Barcode = update.Barcode;
        row.NormalizedBarcode = update.NormalizedBarcode;
        row.HistoricalSkus = JsonSerializer.Serialize(update.HistoricalSkus, V2SqliteStore.Json);
        row.HistoricalBarcodes = JsonSerializer.Serialize(update.HistoricalBarcodes, V2SqliteStore.Json);
        row.UnitId = update.UnitId;
        row.DecimalPlaces = update.DecimalPlaces;
        row.SerialTracking = update.SerialTracking;
        if (update.ReplaceImage)
        {
            row.Image = update.Image?.Content;
            row.ImageContentType = update.Image?.ContentType;
        }

        if (update.Archive && !row.IsArchived)
        {
            row.IsArchived = true;
            row.ArchivedAt = update.Now;
        }

        row.UpdatedAt = update.Now;
        row.Version++;

        var wanted = update.CategoryIds.Distinct().ToHashSet();
        var links = await db.ProductCategories.Where(pc => pc.ProductId == update.Id).ToListAsync(cancellationToken);
        db.ProductCategories.RemoveRange(links.Where(l => !wanted.Contains(l.CategoryId)));
        foreach (var categoryId in wanted.Where(c => links.All(l => l.CategoryId != c)))
        {
            db.ProductCategories.Add(new V2ProductCategoryRow { ProductId = update.Id, CategoryId = categoryId, CreatedAt = update.Now });
        }

        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<long>> ClearZeroStockBinsAsync(long productId, bool dropProduct, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bins = db.Bins.Where(b => b.ProductId == productId && b.QuantityMicros == 0);
        var ids = await bins.Select(b => b.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return ids;
        }

        if (dropProduct)
        {
            await Guard(() => bins.ExecuteUpdateAsync(
                s => s.SetProperty(b => b.ProductId, (long?)null)
                    .SetProperty(b => b.CapacityMicros, (long?)null)
                    .SetProperty(b => b.LowThresholdMicros, (long?)null)
                    .SetProperty(b => b.HighThresholdMicros, (long?)null)
                    .SetProperty(b => b.UpdatedAt, now)
                    .SetProperty(b => b.Version, b => b.Version + 1),
                cancellationToken));
        }
        else
        {
            await Guard(() => bins.ExecuteUpdateAsync(
                s => s.SetProperty(b => b.CapacityMicros, (long?)null)
                    .SetProperty(b => b.LowThresholdMicros, (long?)null)
                    .SetProperty(b => b.HighThresholdMicros, (long?)null)
                    .SetProperty(b => b.UpdatedAt, now)
                    .SetProperty(b => b.Version, b => b.Version + 1),
                cancellationToken));
        }

        return ids;
    }

    public async Task<IReadOnlyList<long>> ResolveCapacityAlertsAsync(long productId, DateTimeOffset now, string note, CancellationToken cancellationToken)
    {
        var alerts = db.Alerts.Where(a => a.ProductId == productId && a.Status == "open" && V2SqliteStore.CapacityAlertTypes.Contains(a.Type));
        var ids = await alerts.Select(a => a.Id).ToListAsync(cancellationToken);
        if (ids.Count > 0)
        {
            await Guard(() => alerts.ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Status, "resolved")
                    .SetProperty(a => a.ResolvedAt, (DateTimeOffset?)now)
                    .SetProperty(a => a.ResolutionNote, note)
                    .SetProperty(a => a.Version, a => a.Version + 1),
                cancellationToken));
        }

        return ids;
    }

    public void AddActivity(V2ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.ActivityLogs.Add(V2SqliteStore.ToRow(entry));
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(cancellationToken);
        await Guard(async () =>
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await transaction.DisposeAsync(); // rolls back unless committed
            await db.DisposeAsync();
        }
        finally
        {
            release();
        }
    }

    // ---- shared queries (also used by the reader) ----

    internal static async Task<V2UnitRecord?> FindUnitAsync(WmsV2DbContext db, long id, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new V2UnitRecord(u.Id, u.Name, u.IsSystem, u.Version, db.Products.Count(p => p.UnitId == u.Id)))
            .SingleOrDefaultAsync(cancellationToken);

    internal static async Task<IReadOnlyList<V2CategoryRecord>> ListCategoriesAsync(WmsV2DbContext db, CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new V2CategoryRecord(
                c.Id,
                c.Name,
                c.ParentId,
                c.Version,
                db.ProductCategories.Count(pc => pc.CategoryId == c.Id),
                db.Categories.Count(child => child.ParentId == c.Id)))
            .ToListAsync(cancellationToken);

    internal static async Task<V2ProductDetail?> FindProductAsync(WmsV2DbContext db, long id, CancellationToken cancellationToken)
    {
        var row = await ProjectProducts(db, db.Products.AsNoTracking().Where(p => p.Id == id)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var image = row.ImageContentType is null
            ? null
            : await db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.Image).SingleAsync(cancellationToken);
        var bins = await db.Bins.AsNoTracking()
            .Where(b => b.ProductId == id && !b.IsArchived)
            .OrderBy(b => b.Code)
            .Select(b => new V2BinStockRecord(
                b.Id, b.Code, b.QuantityMicros, b.CapacityMicros, b.LowThresholdMicros, b.HighThresholdMicros, b.FrozenCountTaskId))
            .ToListAsync(cancellationToken);

        // An unfinished count or unresolved investigation involves the product when the task's bin holds it, the open
        // round counts it, or any submitted result of the task is about it.
        var binIds = db.Bins.Where(b => b.ProductId == id).Select(b => b.Id);
        var open = await db.CountTasks.AsNoTracking()
            .Where(t => V2SqliteStore.OpenCountStatuses.Contains(t.Status) || t.HasUnresolvedInvestigation)
            .AnyAsync(
                t => t.RoundProductId == id || binIds.Contains(t.BinId)
                    || db.CountResults.Any(r => r.CountTaskId == t.Id && r.ProductId == id),
                cancellationToken);

        return new V2ProductDetail(
            ToRecord(row, await CategoryLinksAsync(db, [id], cancellationToken)),
            image is null ? null : new V2ProductImage(image, row.ImageContentType!),
            bins,
            open);
    }

    internal static IQueryable<ProductProjection> ProjectProducts(WmsV2DbContext db, IQueryable<V2ProductRow> products) =>
        products.Join(db.Units, p => p.UnitId, u => u.Id, (p, u) => new ProductProjection
        {
            Id = p.Id,
            Name = p.Name,
            Sku = p.Sku,
            Barcode = p.Barcode,
            HistoricalSkus = p.HistoricalSkus,
            HistoricalBarcodes = p.HistoricalBarcodes,
            UnitId = p.UnitId,
            UnitName = u.Name,
            DecimalPlaces = p.DecimalPlaces,
            SerialTracking = p.SerialTracking,
            ImageContentType = p.ImageContentType,
            ImageLength = p.Image == null ? 0 : p.Image.Length,
            IsArchived = p.IsArchived,
            CreatedById = p.CreatedById,
            CreationRequestKey = p.CreationRequestKey,
            CreationRequestDigest = p.CreationRequestDigest,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            Version = p.Version,
        });

    internal static async Task<ILookup<long, long>> CategoryLinksAsync(WmsV2DbContext db, List<long> productIds, CancellationToken cancellationToken) =>
        (await db.ProductCategories.AsNoTracking()
            .Where(pc => productIds.Contains(pc.ProductId))
            .OrderBy(pc => pc.CategoryId)
            .Select(pc => new { pc.ProductId, pc.CategoryId })
            .ToListAsync(cancellationToken))
        .ToLookup(pc => pc.ProductId, pc => pc.CategoryId);

    internal static V2ProductRecord ToRecord(ProductProjection p, ILookup<long, long> links) => new(
        p.Id,
        p.Name,
        p.Sku,
        p.Barcode,
        Strings(p.HistoricalSkus),
        Strings(p.HistoricalBarcodes),
        p.UnitId,
        p.UnitName,
        p.DecimalPlaces,
        p.SerialTracking,
        links[p.Id].ToList(),
        p.ImageContentType,
        p.ImageLength,
        p.IsArchived,
        p.CreatedById,
        p.CreationRequestKey,
        p.CreationRequestDigest,
        p.CreatedAt,
        p.UpdatedAt,
        p.Version);

    private static string[] Strings(string json) => JsonSerializer.Deserialize<string[]>(json, V2SqliteStore.Json) ?? [];

    private Task SaveAsync(CancellationToken cancellationToken) => Guard(() => db.SaveChangesAsync(cancellationToken));

    /// <summary>Maps lock timeouts, version mismatches and unique-index races to <see cref="PersistenceConflictException"/>.</summary>
    private static async Task<int> Guard(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new PersistenceConflictException("A row changed underneath this write; nothing was committed.", e);
        }
        catch (Exception e) when (V2SqliteStore.IsConflict(e))
        {
            throw new PersistenceConflictException("A concurrent write won or the v2 database stayed locked; nothing was committed.", e);
        }
    }

    /// <summary>A product row without image bytes, joined with its unit name.</summary>
    internal sealed class ProductProjection
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Sku { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string HistoricalSkus { get; init; } = "[]";
        public string HistoricalBarcodes { get; init; } = "[]";
        public long UnitId { get; init; }
        public string UnitName { get; init; } = "";
        public int DecimalPlaces { get; init; }
        public bool SerialTracking { get; init; }
        public string? ImageContentType { get; init; }
        public int ImageLength { get; init; }
        public bool IsArchived { get; init; }
        public long CreatedById { get; init; }
        public string? CreationRequestKey { get; init; }
        public string? CreationRequestDigest { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public int Version { get; init; }
    }
}
