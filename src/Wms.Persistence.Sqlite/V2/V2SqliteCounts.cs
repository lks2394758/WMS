using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 count reads (B4a).</summary>
internal sealed partial class V2SqliteStore : IV2CountReader
{
    public async IAsyncEnumerable<V2CountTaskListRow> StreamCountTasksAsync(
        V2CountTaskFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var tasks = db.CountTasks.AsNoTracking();
        if (filter.Status is { } status)
        {
            tasks = tasks.Where(t => t.Status == status);
        }

        if (filter.BinId is { } bin)
        {
            tasks = tasks.Where(t => t.BinId == bin);
        }

        if (filter.Unresolved is { } unresolved)
        {
            tasks = tasks.Where(t => t.HasUnresolvedInvestigation == unresolved);
        }

        if (filter.BeforeId is { } before)
        {
            tasks = tasks.Where(t => t.Id < before);
        }

        var rows = tasks.OrderByDescending(t => t.Id).Select(t => new
        {
            Task = t,
            Latest = db.CountResults.Where(r => r.CountTaskId == t.Id).OrderByDescending(r => r.Round).Select(r => (long?)r.Id).FirstOrDefault(),
        });
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return new V2CountTaskListRow(V2SqliteCatalogScope.ToCountTask(row.Task), row.Latest);
        }
    }

    public async Task<V2CountTaskDetail?> FindCountTaskAsync(long id, CancellationToken cancellationToken)
    {
        // Task, results and evidence are separate SELECTs: one deferred (read-only, no writer lock) transaction makes them a
        // single SQLite snapshot, so a concurrent commit is seen entirely or not at all.
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        await using var snapshot = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true);
        await db.Database.UseTransactionAsync(snapshot, cancellationToken);
        return await V2SqliteCatalogScope.FindCountTaskDetailAsync(db, id, cancellationToken);
    }
}

/// <summary>Count writes inside the shared write transaction (B4a).</summary>
internal sealed partial class V2SqliteCatalogScope
{
    public Task<V2CountTaskDetail?> FindCountTaskDetailAsync(long id, CancellationToken cancellationToken) =>
        FindCountTaskDetailAsync(Db, id, cancellationToken);

    public async Task<V2CountTaskRecord?> FindCountTaskByCreationRequestAsync(long creatorId, string requestKey, CancellationToken cancellationToken)
    {
        var row = await Db.CountTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.CreatorId == creatorId && t.CreationRequestKey == requestKey, cancellationToken);
        return row is null ? null : ToCountTask(row);
    }

    public async Task<V2CountTaskRecord?> FindBlockingCountTaskAsync(long binId, CancellationToken cancellationToken)
    {
        var row = await Db.CountTasks.AsNoTracking()
            .Where(t => t.BinId == binId && (V2SqliteStore.OpenCountStatuses.Contains(t.Status) || t.HasUnresolvedInvestigation))
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToCountTask(row);
    }

    public async Task<long> InsertCountTaskAsync(V2NewCountTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var row = new V2CountTaskRow
        {
            Number = task.Number,
            BinId = task.BinId,
            BinCode = task.BinCode,
            CreatorId = task.CreatorId,
            CreatorName = task.CreatorName,
            CreatorRole = task.CreatorRole,
            StartedAt = task.Round.StartedAt,
            Status = "inProgress",
            CurrentRound = 1,
            CreationRequestKey = task.CreationRequestKey,
            CreationRequestDigest = task.CreationRequestDigest,
            Version = 1,
        };
        ApplyRound(row, task.Round);
        Db.CountTasks.Add(row);
        await SaveAsync(cancellationToken); // unique indexes reject a concurrent second open task or key here
        return row.Id;
    }

    public async Task<long> InsertCountResultAsync(V2CountResultRecord result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var row = new V2CountResultRow
        {
            CountTaskId = result.CountTaskId,
            Round = result.Round,
            BinId = result.BinId,
            BinCode = result.BinCode,
            ProductId = result.ProductId,
            ProductName = result.ProductName,
            Sku = result.Sku,
            Barcode = result.Barcode,
            UnitName = result.UnitName,
            DecimalPlaces = result.DecimalPlaces,
            SerialTracking = result.SerialTracking,
            RoundStartedAt = result.RoundStartedAt,
            BookQuantityMicros = result.BookQuantityMicros,
            ActualQuantityMicros = result.ActualQuantityMicros,
            DifferenceMicros = result.DifferenceMicros,
            SubmittedById = result.SubmittedById,
            SubmittedByName = result.SubmittedByName,
            SubmittedByRole = result.SubmittedByRole,
            SubmittedAt = result.SubmittedAt,
            Note = result.Note,
        };
        Db.CountResults.Add(row);
        await SaveAsync(cancellationToken); // unique (task, round): a round is submitted once
        return row.Id;
    }

    public async Task<bool> UpdateCountTaskAsync(long id, int expectedVersion, V2CountTaskUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var row = await Db.CountTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null || row.Version != expectedVersion)
        {
            return false;
        }

        row.Status = update.Status;
        row.CompletedAt = update.CompletedAt;
        row.CurrentRound = update.CurrentRound;
        ApplyRound(row, update.Round);
        row.StatusBeforeRound = update.StatusBeforeRound;
        row.CompletedAtBeforeRound = update.CompletedAtBeforeRound;
        row.CancelledAt = update.CancelledAt;
        row.CancelReason = update.CancelReason;
        row.HasUnresolvedInvestigation = update.HasUnresolvedInvestigation ?? row.HasUnresolvedInvestigation;
        row.Version++;
        await SaveAsync(cancellationToken);
        return true;
    }

    public async Task SetBinFrozenAsync(long binId, long? countTaskId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bin = await Db.Bins.SingleAsync(b => b.Id == binId, cancellationToken);
        bin.FrozenCountTaskId = countTaskId;
        bin.UpdatedAt = now;
        bin.Version++;
        await SaveAsync(cancellationToken);
    }

    public async Task<V2CountEvidence?> FindCountConfirmByRequestAsync(long actorId, string requestKey, CancellationToken cancellationToken)
    {
        var logs = await Db.ActivityLogs.AsNoTracking()
            .Where(a => a.Type == "countConfirm" && a.Success && a.ActorId == actorId && a.Changes != null && a.Changes.Contains(requestKey))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return logs.Select(ToEvidence).FirstOrDefault(e => e.RequestKey == requestKey);
    }

    public async Task<long> AddCountEvidenceAsync(
        V2ActivityEntry entry, long countTaskId, long? countResultId, long? transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var row = V2SqliteStore.ToRow(entry);
        row.CountTaskId = countTaskId;
        row.CountResultId = countResultId;
        row.TransactionId = transactionId;
        Db.ActivityLogs.Add(row);
        await SaveAsync(cancellationToken);
        return row.Id;
    }

    public Task<long?> FindOpenDiscrepancyAlertAsync(long countTaskId, CancellationToken cancellationToken) =>
        Db.Alerts.AsNoTracking()
            .Where(a => a.CountTaskId == countTaskId && a.Type == "countDiscrepancy" && a.Status == "open")
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<long> InsertDiscrepancyAlertAsync(V2NewDiscrepancyAlert alert, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var row = new V2AlertRow
        {
            Type = "countDiscrepancy",
            BinId = alert.BinId,
            ProductId = alert.ProductId,
            CountTaskId = alert.CountTaskId,
            CountResultId = alert.CountResultId,
            Message = alert.Message,
            TriggerSnapshot = alert.TriggerSnapshotJson,
            CreatedAt = alert.Now,
            Status = "open",
            Version = 1,
        };
        Db.Alerts.Add(row);
        await SaveAsync(cancellationToken); // one open discrepancy alert per task (unique index)
        return row.Id;
    }

    internal static V2CountEvidence ToEvidence(V2ActivityLogRow log)
    {
        var changes = string.IsNullOrEmpty(log.Changes)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<V2FieldChange[]>(log.Changes, V2SqliteStore.Json) ?? [];
        string? Value(string field) => changes.FirstOrDefault(c => c.Field == field)?.After;
        static long? Id(string? text) => long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        var resolved = (Value("resolvedResultIds") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Id).OfType<long>().ToList();
        return new V2CountEvidence(
            log.Id, log.Type, log.ActorId, Id(Value("resultId")), Value("outcome"), Id(Value("transactionId")), resolved,
            Value("requestKey"), Value("requestDigest"));
    }

    internal static async Task<V2CountTaskDetail?> FindCountTaskDetailAsync(WmsV2DbContext db, long id, CancellationToken cancellationToken)
    {
        var task = await db.CountTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var results = await db.CountResults.AsNoTracking()
            .Where(r => r.CountTaskId == id)
            .OrderBy(r => r.Round)
            .Select(r => new
            {
                Row = r,
                Adjustment = db.InventoryTransactions.Where(x => x.CountResultId == r.Id).Select(x => (long?)x.Id).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var evidence = (await db.ActivityLogs.AsNoTracking()
                .Where(a => a.CountTaskId == id && a.Success && (a.Type == "countConfirm" || a.Type == "countResolve"))
                .OrderBy(a => a.Id)
                .ToListAsync(cancellationToken))
            .Select(ToEvidence)
            .ToList();
        return new V2CountTaskDetail(
            ToCountTask(task),
            results.Select(x => new V2CountResultRecord(
                x.Row.Id, x.Row.CountTaskId, x.Row.Round, x.Row.BinId, x.Row.BinCode, x.Row.ProductId, x.Row.ProductName, x.Row.Sku,
                x.Row.Barcode, x.Row.UnitName, x.Row.DecimalPlaces, x.Row.SerialTracking, x.Row.RoundStartedAt, x.Row.BookQuantityMicros,
                x.Row.ActualQuantityMicros, x.Row.DifferenceMicros, x.Row.SubmittedById, x.Row.SubmittedByName, x.Row.SubmittedByRole,
                x.Row.SubmittedAt, x.Row.Note, x.Adjustment)).ToList(),
            evidence);
    }

    internal static V2CountTaskRecord ToCountTask(V2CountTaskRow t) => new(
        t.Id, t.Number, t.BinId, t.BinCode, t.CreatorId, t.CreatorName, t.CreatorRole, t.StartedAt, t.Status, t.CompletedAt, t.CurrentRound,
        t.HasUnresolvedInvestigation,
        t.RoundStartedAt is { } started && t.RoundProductId is { } product
            ? new V2CountRoundSnapshot(
                started, t.RoundStartedById ?? 0, t.RoundStartedByName ?? "", t.RoundStartedByRole ?? "", product, t.RoundProductName ?? "",
                t.RoundSku ?? "", t.RoundBarcode ?? "", t.RoundUnitName ?? "", t.RoundDecimalPlaces ?? 0, t.RoundSerialTracking ?? false,
                t.RoundBookQuantityMicros ?? 0)
            : null,
        t.StatusBeforeRound, t.CompletedAtBeforeRound, t.CancelledAt, t.CancelReason, t.CreationRequestKey, t.CreationRequestDigest, t.Version);

    private static void ApplyRound(V2CountTaskRow row, V2CountRoundSnapshot? round)
    {
        row.RoundStartedAt = round?.StartedAt;
        row.RoundStartedById = round?.StartedById;
        row.RoundStartedByName = round?.StartedByName;
        row.RoundStartedByRole = round?.StartedByRole;
        row.RoundProductId = round?.ProductId;
        row.RoundProductName = round?.ProductName;
        row.RoundSku = round?.Sku;
        row.RoundBarcode = round?.Barcode;
        row.RoundUnitName = round?.UnitName;
        row.RoundDecimalPlaces = round?.DecimalPlaces;
        row.RoundSerialTracking = round?.SerialTracking;
        row.RoundBookQuantityMicros = round?.BookQuantityMicros;
    }
}
