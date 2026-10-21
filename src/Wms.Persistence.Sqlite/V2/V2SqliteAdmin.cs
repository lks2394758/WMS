using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 bins, accounts and settings reads (B2b).</summary>
internal sealed partial class V2SqliteStore : IV2AdminReader
{
    public async IAsyncEnumerable<V2BinRecord> StreamBinsAsync(long? productId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var bins = db.Bins.AsNoTracking().Where(b => !b.IsArchived);
        if (productId is { } product)
        {
            bins = bins.Where(b => b.ProductId == product);
        }

        var ordered = V2SqliteCatalogScope.ProjectBins(db, bins.OrderBy(b => b.NormalizedCode).ThenBy(b => b.Id)).AsAsyncEnumerable();
        await foreach (var row in ordered.WithCancellation(cancellationToken))
        {
            yield return V2SqliteCatalogScope.ToRecord(row);
        }
    }

    public async Task<V2BinRecord?> FindBinAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.FindBinAsync(db, id, cancellationToken);
    }

    public async Task<IReadOnlyList<V2UserSummary>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.UserSummaries(db, db.Users.AsNoTracking().OrderBy(u => u.Id), now, cancellationToken);
    }

    public async Task<V2SettingsRecord> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.GetSettingsAsync(db, cancellationToken);
    }
}

/// <summary>v2 bins, accounts and settings changes inside the catalog write transaction (B2b).</summary>
internal sealed partial class V2SqliteCatalogScope
{
    public Task<V2BinRecord?> FindBinAsync(long id, CancellationToken cancellationToken) => FindBinAsync(Db, id, cancellationToken);

    public async Task<IReadOnlyList<V2BinCodeOwner>> ListBinCodesAsync(CancellationToken cancellationToken)
    {
        var rows = await Db.Bins.AsNoTracking().Select(b => new { b.Id, b.Code, b.HistoricalCodes }).ToListAsync(cancellationToken);
        return rows.Select(r => new V2BinCodeOwner(r.Id, r.Code, Strings(r.HistoricalCodes))).ToList();
    }

    public async Task<IReadOnlyList<long>> InsertBinsAsync(IReadOnlyList<V2NewBin> bins, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bins);
        var rows = bins.Select(b => new V2BinRow
        {
            Zone = b.Zone,
            Rack = b.Rack,
            Level = b.Level,
            Cell = b.Cell,
            Code = b.Code,
            NormalizedCode = b.NormalizedCode,
            HistoricalCodes = "[]",
            QuantityMicros = 0,
            IsArchived = false,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        }).ToList();
        Db.Bins.AddRange(rows);
        await SaveAsync(cancellationToken);
        return rows.Select(r => r.Id).ToList();
    }

    public async Task<bool> UpdateBinPositionAsync(V2BinPositionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Db.Bins.SingleOrDefaultAsync(b => b.Id == update.Id, cancellationToken);
        if (row is null || row.Version != update.ExpectedVersion)
        {
            return false;
        }

        row.Zone = update.Zone;
        row.Rack = update.Rack;
        row.Level = update.Level;
        row.Cell = update.Cell;
        row.Code = update.Code;
        row.NormalizedCode = update.NormalizedCode;
        row.HistoricalCodes = JsonSerializer.Serialize(update.HistoricalCodes, V2SqliteStore.Json);
        row.UpdatedAt = update.Now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ArchiveBinAsync(long id, int expectedVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await Db.Bins.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (row is null || row.Version != expectedVersion)
        {
            return false;
        }

        row.IsArchived = true;
        row.ArchivedAt = now;
        row.UpdatedAt = now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<long>> ResolveBinCapacityAlertsAsync(long binId, DateTimeOffset now, string note, CancellationToken cancellationToken)
    {
        var alerts = Db.Alerts.Where(a => a.BinId == binId && a.Status == "open" && V2SqliteStore.CapacityAlertTypes.Contains(a.Type));
        var ids = await alerts.Select(a => a.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            await ResolveAlertAsync(id, now, note, cancellationToken);
        }

        return ids;
    }

    public Task<V2SettingsRecord> GetSettingsAsync(CancellationToken cancellationToken) => GetSettingsAsync(Db, cancellationToken);

    public async Task<bool> UpdateSettingsAsync(V2SettingsUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Db.SystemSettings.SingleAsync(s => s.Id == V2SystemSettingsRow.SingletonId, cancellationToken);
        if (row.Version != update.ExpectedVersion)
        {
            return false;
        }

        row.DefaultRackLevels = update.DefaultRackLevels;
        row.DefaultCellsPerLevel = update.DefaultCellsPerLevel;
        row.LowThresholdMicros = update.LowThresholdMicros;
        row.HighThresholdMicros = update.HighThresholdMicros;
        row.SessionHours = update.SessionHours;
        row.UpdatedById = update.UpdatedById;
        row.UpdatedAt = update.Now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public Task<IReadOnlyList<V2CapacityState>> ListCapacityStatesAsync(CancellationToken cancellationToken) =>
        CapacityStatesAsync(Db.Bins.AsNoTracking().Where(b => !b.IsArchived), cancellationToken);

    public Task<IReadOnlyList<V2CapacityState>> ListCapacityStatesAsync(IReadOnlyCollection<long> binIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binIds);
        var ids = binIds.Distinct().ToList();
        return CapacityStatesAsync(Db.Bins.AsNoTracking().Where(b => !b.IsArchived && ids.Contains(b.Id)), cancellationToken);
    }

    private async Task<IReadOnlyList<V2CapacityState>> CapacityStatesAsync(IQueryable<V2BinRow> bins, CancellationToken cancellationToken) =>
        await bins
            .OrderBy(b => b.Id)
            .Select(b => new V2CapacityState(
                b.Id,
                b.Code,
                b.ProductId,
                Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.Sku).FirstOrDefault(),
                Db.Products.Where(p => p.Id == b.ProductId).Join(Db.Units, p => p.UnitId, u => u.Id, (p, u) => u.Name).FirstOrDefault(),
                Db.Products.Where(p => p.Id == b.ProductId).Select(p => (int?)p.DecimalPlaces).FirstOrDefault(),
                b.QuantityMicros,
                b.CapacityMicros,
                b.LowThresholdMicros,
                b.HighThresholdMicros,
                Db.Alerts.Where(a => a.BinId == b.Id && a.Status == "open" && V2SqliteStore.CapacityAlertTypes.Contains(a.Type))
                    .Select(a => (long?)a.Id).FirstOrDefault(),
                Db.Alerts.Where(a => a.BinId == b.Id && a.Status == "open" && V2SqliteStore.CapacityAlertTypes.Contains(a.Type))
                    .Select(a => a.Type).FirstOrDefault()))
            .ToListAsync(cancellationToken);

    public async Task ResolveAlertAsync(long alertId, DateTimeOffset now, string note, CancellationToken cancellationToken) =>
        await Guard(() => Db.Alerts.Where(a => a.Id == alertId && a.Status == "open").ExecuteUpdateAsync(
            s => s.SetProperty(a => a.Status, "resolved")
                .SetProperty(a => a.ResolvedAt, (DateTimeOffset?)now)
                .SetProperty(a => a.ResolutionNote, note)
                .SetProperty(a => a.Version, a => a.Version + 1),
            cancellationToken));

    public async Task<long> InsertAlertAsync(V2NewAlert alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var row = new V2AlertRow
        {
            Type = alert.Type,
            BinId = alert.BinId,
            ProductId = alert.ProductId,
            Message = alert.Message,
            TriggerSnapshot = alert.TriggerSnapshotJson,
            CreatedAt = alert.Now,
            Status = "open",
            Version = 1,
        };
        Db.Alerts.Add(row);
        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public async Task<IReadOnlyList<V2UserRecord>> ListUsersAsync(CancellationToken cancellationToken) =>
        (await Db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(cancellationToken)).Select(u => V2SqliteStore.ToRecord(u)).ToList();

    public async Task<V2UserSummary?> FindUserSummaryAsync(long id, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await UserSummaries(Db, Db.Users.AsNoTracking().Where(u => u.Id == id), now, cancellationToken)).SingleOrDefault();

    public async Task<long> InsertUserAsync(V2NewUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var row = new V2UserRow
        {
            Username = user.Username,
            NormalizedUsername = user.NormalizedUsername,
            DisplayName = user.DisplayName,
            PasswordHash = user.PasswordHash,
            Roles = JsonSerializer.Serialize(user.Roles, V2SqliteStore.Json),
            Enabled = user.Enabled,
            CreatedAt = user.Now,
            UpdatedAt = user.Now,
            Version = 1,
        };
        Db.Users.Add(row);
        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public async Task<bool> UpdateUserAsync(V2UserUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Db.Users.SingleOrDefaultAsync(u => u.Id == update.Id, cancellationToken);
        if (row is null || row.Version != update.ExpectedVersion)
        {
            return false;
        }

        row.Username = update.Username;
        row.NormalizedUsername = update.NormalizedUsername;
        row.DisplayName = update.DisplayName;
        row.Roles = JsonSerializer.Serialize(update.Roles, V2SqliteStore.Json);
        row.Enabled = update.Enabled;
        if (update.PasswordHash is not null)
        {
            row.PasswordHash = update.PasswordHash;
        }

        row.UpdatedAt = update.Now;
        row.Version++; // also defeats a login whose password check read the old account (B1 expected-version check)
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<int> RevokeSessionsAsync(long userId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        await Guard(() => Db.LoginSessions.Where(s => s.UserId == userId && s.LoggedOutAt == null && s.RevokedAt == null).ExecuteUpdateAsync(
            s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now)
                .SetProperty(x => x.RevokeReason, reason)
                .SetProperty(x => x.Version, x => x.Version + 1),
            cancellationToken));

    // ---- shared queries (also used by the reader) ----

    internal static async Task<V2BinRecord?> FindBinAsync(WmsV2DbContext db, long id, CancellationToken cancellationToken)
    {
        var row = await ProjectBins(db, db.Bins.AsNoTracking().Where(b => b.Id == id)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    internal static IQueryable<BinProjection> ProjectBins(WmsV2DbContext db, IQueryable<V2BinRow> bins) =>
        bins.Select(b => new BinProjection
        {
            Bin = b,
            ProductName = db.Products.Where(p => p.Id == b.ProductId).Select(p => p.Name).FirstOrDefault(),
            Sku = db.Products.Where(p => p.Id == b.ProductId).Select(p => p.Sku).FirstOrDefault(),
            UnitName = db.Products.Where(p => p.Id == b.ProductId).Join(db.Units, p => p.UnitId, u => u.Id, (p, u) => u.Name).FirstOrDefault(),
            DecimalPlaces = db.Products.Where(p => p.Id == b.ProductId).Select(p => (int?)p.DecimalPlaces).FirstOrDefault(),
            SerialTracking = db.Products.Where(p => p.Id == b.ProductId).Select(p => (bool?)p.SerialTracking).FirstOrDefault(),
            HasOpenCount = db.CountTasks.Any(t => t.BinId == b.Id && V2SqliteStore.OpenCountStatuses.Contains(t.Status)),
            HasUnresolvedInvestigation = db.CountTasks.Any(t => t.BinId == b.Id && t.HasUnresolvedInvestigation),
            OpenCountTaskId = db.CountTasks
                .Where(t => t.BinId == b.Id && (V2SqliteStore.OpenCountStatuses.Contains(t.Status) || t.HasUnresolvedInvestigation))
                .Select(t => (long?)t.Id)
                .FirstOrDefault(),
        });

    internal static V2BinRecord ToRecord(BinProjection row)
    {
        var b = row.Bin;
        return new V2BinRecord(
            b.Id, b.Zone, b.Rack, b.Level, b.Cell, b.Code, Strings(b.HistoricalCodes),
            b.ProductId, row.ProductName, row.Sku, row.UnitName, row.DecimalPlaces, row.SerialTracking,
            b.QuantityMicros, b.CapacityMicros, b.LowThresholdMicros, b.HighThresholdMicros, b.FrozenCountTaskId,
            b.IsArchived, row.HasOpenCount, row.HasUnresolvedInvestigation, b.Version, row.OpenCountTaskId);
    }

    internal static async Task<V2SettingsRecord> GetSettingsAsync(WmsV2DbContext db, CancellationToken cancellationToken) =>
        await db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == V2SystemSettingsRow.SingletonId)
            .Select(s => new V2SettingsRecord(
                s.DefaultRackLevels,
                s.DefaultCellsPerLevel,
                s.LowThresholdMicros,
                s.HighThresholdMicros,
                s.SessionHours,
                s.Version,
                s.UpdatedAt,
                s.UpdatedById,
                db.Users.Where(u => u.Id == s.UpdatedById).Select(u => u.DisplayName).FirstOrDefault()))
            .SingleAsync(cancellationToken);

    internal static async Task<IReadOnlyList<V2UserSummary>> UserSummaries(
        WmsV2DbContext db, IQueryable<V2UserRow> users, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await users
            .Select(u => new
            {
                User = u,
                Active = db.LoginSessions.Any(s => s.UserId == u.Id && s.LoggedOutAt == null && s.RevokedAt == null && s.ExpiresAt > now),
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r =>
        {
            var record = V2SqliteStore.ToRecord(r.User);
            return new V2UserSummary(
                record.Id, record.Username, record.DisplayName, record.Roles, record.Enabled, r.Active,
                r.User.CreatedAt, r.User.UpdatedAt, record.Version);
        }).ToList();
    }

    /// <summary>A bin row with its product snapshot fields and count state.</summary>
    internal sealed class BinProjection
    {
        public V2BinRow Bin { get; init; } = null!;
        public string? ProductName { get; init; }
        public string? Sku { get; init; }
        public string? UnitName { get; init; }
        public int? DecimalPlaces { get; init; }
        public bool? SerialTracking { get; init; }
        public bool HasOpenCount { get; init; }
        public bool HasUnresolvedInvestigation { get; init; }
        public long? OpenCountTaskId { get; init; }
    }
}
