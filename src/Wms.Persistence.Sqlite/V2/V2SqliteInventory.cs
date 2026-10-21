using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 ledger reads (B3a).</summary>
internal sealed partial class V2SqliteStore : IV2InventoryReader
{
    public async Task<IReadOnlyList<V2TransactionSummary>> ListTransactionsAsync(V2TransactionFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var query = db.InventoryTransactions.AsNoTracking();
        if (filter.Type is { } type)
        {
            query = query.Where(t => t.Type == type);
        }

        if (filter.From is { } from)
        {
            query = query.Where(t => t.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(t => t.OccurredAt < to);
        }

        if (filter.ActorId is { } actor)
        {
            query = query.Where(t => t.ActorId == actor);
        }

        if (filter.ProductId is { } product)
        {
            query = query.Where(t => db.InventoryTransactionLines.Any(l => l.TransactionId == t.Id && l.ProductId == product));
        }

        if (filter.BinId is { } bin)
        {
            query = query.Where(t => db.InventoryTransactionLines.Any(l => l.TransactionId == t.Id && (l.FromBinId == bin || l.ToBinId == bin)));
        }

        if (filter.BeforeId is { } before)
        {
            query = query.Where(t => t.Id < before);
        }

        var headers = await query.OrderByDescending(t => t.Id).Take(filter.BatchSize).ToListAsync(cancellationToken);
        var ids = headers.Select(h => h.Id).ToList();
        var lines = (await db.InventoryTransactionLines.AsNoTracking()
                .Where(l => ids.Contains(l.TransactionId))
                .Select(l => new { l.TransactionId, l.ProductName, l.Sku })
                .ToListAsync(cancellationToken))
            .ToLookup(l => l.TransactionId);

        return headers.Select(h => new V2TransactionSummary(
                h.Id, h.Number, h.Type, h.ActorId, h.ActorName, h.ActorRole, h.OccurredAt, lines[h.Id].Count(),
                h.Source, h.Destination, h.Reason, h.CountResultId,
                lines[h.Id].SelectMany(l => new[] { l.ProductName, l.Sku }).ToList()))
            .ToList();
    }

    public async Task<V2TransactionRecord?> FindTransactionAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        return await V2SqliteCatalogScope.LoadTransactionAsync(db, id, cancellationToken);
    }
}

/// <summary>v2 inventory changes inside the shared write transaction (B3a).</summary>
internal sealed partial class V2SqliteCatalogScope
{
    public async Task<IReadOnlyDictionary<long, V2ProductRecord>> FindProductRecordsAsync(
        IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToList();
        var rows = await ProjectProducts(Db, Db.Products.AsNoTracking().Where(p => wanted.Contains(p.Id))).ToListAsync(cancellationToken);
        var links = await CategoryLinksAsync(Db, wanted, cancellationToken);
        return rows.ToDictionary(r => r.Id, r => ToRecord(r, links));
    }

    public async Task<(long Id, string RequestDigest)?> FindTransactionByRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken)
    {
        var row = await Db.InventoryTransactions.AsNoTracking()
            .Where(t => t.ActorId == actorId && t.RequestKey == requestKey)
            .Select(t => new { t.Id, t.RequestDigest })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return (row.Id, row.RequestDigest);
    }

    public Task<V2TransactionRecord?> FindTransactionAsync(long id, CancellationToken cancellationToken) => LoadTransactionAsync(Db, id, cancellationToken);

    public async Task<V2CountResultLink?> FindCountResultAsync(long id, CancellationToken cancellationToken) =>
        await Db.CountResults.AsNoTracking()
            .Where(r => r.Id == id)
            .Join(Db.CountTasks.AsNoTracking(), r => r.CountTaskId, t => t.Id, (r, t) => new { r, t })
            .Where(x => x.r.BinId == x.t.BinId)
            .Select(x => new V2CountResultLink(x.r.Id, x.r.CountTaskId, x.t.BinId, x.r.ProductId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<V2SerializedItemRecord>> FindSerializedItemCandidatesAsync(
        IReadOnlyCollection<string> normalizedNumbers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedNumbers);
        var wanted = normalizedNumbers.Distinct().ToList();
        var rows = await Db.SerializedItems.AsNoTracking()
            .Where(s => wanted.Contains(s.NormalizedSerialNumber) || s.HistoricalSerialNumbers != "[]")
            .ToListAsync(cancellationToken);
        return rows.Select(r => ToItemRecord(r)).ToList();
    }

    public async Task<IReadOnlyList<V2SerializedItemRecord>> ListSerialNumbersAsync(CancellationToken cancellationToken) =>
        (await Db.SerializedItems.AsNoTracking().ToListAsync(cancellationToken)).Select(r => ToItemRecord(r)).ToList();

    public async Task<IReadOnlyList<long>> InsertSerializedItemsAsync(
        IReadOnlyList<V2NewSerializedItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = items.Select(i => new V2SerializedItemRow
        {
            SerialNumber = i.SerialNumber,
            NormalizedSerialNumber = i.NormalizedSerialNumber,
            HistoricalSerialNumbers = "[]",
            ProductId = i.ProductId,
            BinId = i.BinId,
            InStock = true,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        }).ToList();
        Db.SerializedItems.AddRange(rows);
        await SaveAsync(cancellationToken);
        return rows.Select(r => r.Id).ToList();
    }

    public async Task<bool> MoveSerializedItemsAsync(IReadOnlyList<V2SerializedItemMove> moves, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moves);
        var ids = moves.Select(m => m.Id).ToList();
        var rows = await Db.SerializedItems.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        foreach (var move in moves)
        {
            if (!rows.TryGetValue(move.Id, out var row) || row.Version != move.ExpectedVersion)
            {
                return false;
            }

            row.BinId = move.BinId;
            row.InStock = move.BinId is not null;
            row.UpdatedAt = now;
            row.Version++;
        }

        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateBinStockAsync(V2BinStockUpdate update, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Db.Bins.SingleOrDefaultAsync(b => b.Id == update.Id, cancellationToken);
        if (row is null || row.Version != update.ExpectedVersion)
        {
            return false;
        }

        row.ProductId = update.ProductId;
        row.QuantityMicros = update.QuantityMicros;
        row.CapacityMicros = update.CapacityMicros;
        row.LowThresholdMicros = update.LowThresholdMicros;
        row.HighThresholdMicros = update.HighThresholdMicros;
        row.UpdatedAt = now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task<long> InsertTransactionAsync(V2NewTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var header = new V2TransactionRow
        {
            Number = transaction.Number,
            Type = transaction.Type,
            ActorId = transaction.ActorId,
            ActorName = transaction.ActorName,
            ActorRole = transaction.ActorRole,
            OccurredAt = transaction.OccurredAt,
            Source = transaction.Source,
            Destination = transaction.Destination,
            Reason = transaction.Reason,
            CountResultId = transaction.CountResultId,
            RequestKey = transaction.RequestKey,
            RequestDigest = transaction.RequestDigest,
        };
        Db.InventoryTransactions.Add(header);
        await SaveAsync(cancellationToken); // the unique (actor, key) index rejects a concurrent duplicate here

        var lines = transaction.Lines.Select(l => (Record: l, Row: new V2TransactionLineRow
        {
            TransactionId = header.Id,
            LineNo = l.LineNo,
            ProductId = l.ProductId,
            ProductName = l.ProductName,
            Sku = l.Sku,
            Barcode = l.Barcode,
            UnitName = l.UnitName,
            DecimalPlaces = l.DecimalPlaces,
            FromBinId = l.FromBinId,
            FromBinCode = l.FromBinCode,
            ToBinId = l.ToBinId,
            ToBinCode = l.ToBinCode,
            QuantityMicros = l.QuantityMicros,
            FromBeforeMicros = l.FromBeforeMicros,
            FromAfterMicros = l.FromAfterMicros,
            ToBeforeMicros = l.ToBeforeMicros,
            ToAfterMicros = l.ToAfterMicros,
            PackagingUnit = l.PackagingUnit,
            PackagingQuantityMicros = l.PackagingQuantityMicros,
            ConversionFactorMicros = l.ConversionFactorMicros,
        })).ToList();
        Db.InventoryTransactionLines.AddRange(lines.Select(l => l.Row));
        await SaveAsync(cancellationToken);

        Db.TransactionLineItems.AddRange(lines.SelectMany(l => l.Record.Items.Select(i => new V2TransactionLineItemRow
        {
            TransactionLineId = l.Row.Id,
            SerializedItemId = i.SerializedItemId,
            SerialNumber = i.SerialNumber,
        })));
        await SaveAsync(cancellationToken);
        return header.Id;
    }

    internal static async Task<V2TransactionRecord?> LoadTransactionAsync(WmsV2DbContext db, long id, CancellationToken cancellationToken)
    {
        var header = await db.InventoryTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await db.InventoryTransactionLines.AsNoTracking()
            .Where(l => l.TransactionId == id)
            .OrderBy(l => l.LineNo)
            .ToListAsync(cancellationToken);
        var lineIds = lines.Select(l => l.Id).ToList();
        var items = (await db.TransactionLineItems.AsNoTracking()
                .Where(i => lineIds.Contains(i.TransactionLineId))
                .OrderBy(i => i.Id)
                .ToListAsync(cancellationToken))
            .ToLookup(i => i.TransactionLineId);
        var countTaskId = header.CountResultId is { } result
            ? await db.CountResults.AsNoTracking().Where(r => r.Id == result).Select(r => (long?)r.CountTaskId).SingleOrDefaultAsync(cancellationToken)
            : null;

        return new V2TransactionRecord(
            header.Id, header.Number, header.Type, header.ActorId, header.ActorName, header.ActorRole, header.OccurredAt,
            header.Source, header.Destination, header.Reason, header.CountResultId, countTaskId, header.RequestDigest,
            lines.Select(l => new V2TransactionLineRecord(
                l.LineNo, l.ProductId, l.ProductName, l.Sku, l.Barcode, l.UnitName, l.DecimalPlaces, l.QuantityMicros,
                l.FromBinId, l.FromBinCode, l.FromBeforeMicros, l.FromAfterMicros,
                l.ToBinId, l.ToBinCode, l.ToBeforeMicros, l.ToAfterMicros,
                l.PackagingUnit, l.PackagingQuantityMicros, l.ConversionFactorMicros,
                items[l.Id].Select(i => new V2LineItem(i.SerializedItemId, i.SerialNumber)).ToList())).ToList());
    }

    internal static V2SerializedItemRecord ToItemRecord(V2SerializedItemRow row) => new(
        row.Id,
        row.SerialNumber,
        JsonSerializer.Deserialize<string[]>(row.HistoricalSerialNumbers, V2SqliteStore.Json) ?? [],
        row.ProductId,
        row.BinId,
        row.InStock,
        row.Version);
}
