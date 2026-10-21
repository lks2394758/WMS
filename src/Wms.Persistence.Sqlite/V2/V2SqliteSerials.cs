using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 serialized item reads (B3b).</summary>
internal sealed partial class V2SqliteStore : IV2SerialReader
{
    public async IAsyncEnumerable<V2SerializedItemView> StreamSerializedItemsAsync(
        long? productId, long? binId, bool? inStock, long? afterId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var items = db.SerializedItems.AsNoTracking();
        if (productId is { } product)
        {
            items = items.Where(s => s.ProductId == product);
        }

        if (binId is { } bin)
        {
            items = items.Where(s => s.BinId == bin);
        }

        if (inStock is { } stocked)
        {
            items = items.Where(s => s.InStock == stocked);
        }

        if (afterId is { } after)
        {
            items = items.Where(s => s.Id > after);
        }

        await foreach (var row in V2SqliteCatalogScope.ProjectItems(db, items.OrderBy(s => s.Id)).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return V2SqliteCatalogScope.ToView(row);
        }
    }

    public async Task<V2SerializedItemDetail?> FindSerializedItemAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var row = await V2SqliteCatalogScope.ProjectItems(db, db.SerializedItems.AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        // History: the immutable item links, lines and transactions, with the number and bin codes as they were then.
        var history = await db.TransactionLineItems.AsNoTracking()
            .Where(li => li.SerializedItemId == id)
            .Join(db.InventoryTransactionLines.AsNoTracking(), li => li.TransactionLineId, l => l.Id, (li, l) => new { li, l })
            .Join(db.InventoryTransactions.AsNoTracking(), x => x.l.TransactionId, t => t.Id, (x, t) => new { x.li, x.l, t })
            .OrderBy(x => x.t.Id)
            .ThenBy(x => x.l.LineNo)
            .Select(x => new V2SerialHistoryRecord(
                x.t.Id, x.t.Number, x.t.Type, x.t.OccurredAt, x.t.ActorName, x.t.ActorRole, x.li.SerialNumber, x.l.FromBinCode, x.l.ToBinCode))
            .ToListAsync(cancellationToken);

        // Corrections: the immutable activity log entries naming this item (subjects are stored as {"type","id","label"}).
        var subject = $"\"type\":\"serializedItem\",\"id\":\"{id.ToString(CultureInfo.InvariantCulture)}\"";
        var logs = await db.ActivityLogs.AsNoTracking()
            .Where(a => a.Type == "serialCorrect" && a.Success && a.Subjects.Contains(subject))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var corrections = logs.Select(log =>
        {
            var changes = string.IsNullOrEmpty(log.Changes) ? [] : JsonSerializer.Deserialize<V2FieldChange[]>(log.Changes, Json) ?? [];
            var number = changes.FirstOrDefault(c => c.Field == "serialNumber");
            var reason = changes.FirstOrDefault(c => c.Field == "reason");
            return new V2SerialCorrectionRecord(
                log.Id, number?.Before ?? string.Empty, number?.After ?? string.Empty, reason?.After ?? string.Empty,
                log.ActorName ?? string.Empty, log.ActorRole ?? string.Empty, log.OccurredAt);
        }).ToList();

        return new V2SerializedItemDetail(V2SqliteCatalogScope.ToView(row), history, corrections);
    }
}

/// <summary>Serial number corrections inside the shared write transaction (B3b).</summary>
internal sealed partial class V2SqliteCatalogScope
{
    public async Task<V2SerializedItemRecord?> FindSerializedItemAsync(long id, CancellationToken cancellationToken)
    {
        var row = await Db.SerializedItems.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return row is null ? null : ToItemRecord(row);
    }

    public async Task<bool> CorrectSerialNumberAsync(
        long id, int expectedVersion, string serialNumber, string normalizedSerialNumber, IReadOnlyList<string> historicalSerialNumbers,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(historicalSerialNumbers);
        var row = await Db.SerializedItems.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (row is null || row.Version != expectedVersion)
        {
            return false;
        }

        row.SerialNumber = serialNumber;
        row.NormalizedSerialNumber = normalizedSerialNumber;
        row.HistoricalSerialNumbers = JsonSerializer.Serialize(historicalSerialNumbers, V2SqliteStore.Json);
        row.UpdatedAt = now;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    internal static IQueryable<ItemProjection> ProjectItems(WmsV2DbContext db, IQueryable<V2SerializedItemRow> items) =>
        items.Select(s => new ItemProjection
        {
            Item = s,
            ProductName = db.Products.Where(p => p.Id == s.ProductId).Select(p => p.Name).FirstOrDefault() ?? string.Empty,
            Sku = db.Products.Where(p => p.Id == s.ProductId).Select(p => p.Sku).FirstOrDefault() ?? string.Empty,
            BinCode = db.Bins.Where(b => b.Id == s.BinId).Select(b => b.Code).FirstOrDefault(),
        });

    internal static V2SerializedItemView ToView(ItemProjection row) => new(ToItemRecord(row.Item), row.ProductName, row.Sku, row.BinCode);

    internal sealed class ItemProjection
    {
        public V2SerializedItemRow Item { get; init; } = null!;
        public string ProductName { get; init; } = "";
        public string Sku { get; init; } = "";
        public string? BinCode { get; init; }
    }
}
