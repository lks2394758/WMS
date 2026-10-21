using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>v2 alert, activity log and dashboard reads (B5a). Read-only; never takes the writer lock.</summary>
internal sealed partial class V2SqliteStore : IV2MonitoringReader
{
    public async Task<IReadOnlyList<V2AlertView>> ListAlertsAsync(V2AlertFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var alerts = db.Alerts.AsNoTracking();
        if (filter.Status is { } status)
        {
            alerts = alerts.Where(a => a.Status == status);
        }

        if (filter.Type is { } type)
        {
            alerts = alerts.Where(a => a.Type == type);
        }

        if (filter.BinId is { } bin)
        {
            alerts = alerts.Where(a => a.BinId == bin);
        }

        if (filter.ProductId is { } product)
        {
            alerts = alerts.Where(a => a.ProductId == product);
        }

        if (filter.BeforeId is { } before)
        {
            alerts = alerts.Where(a => a.Id < before);
        }

        return await ProjectAlerts(db, alerts.OrderByDescending(a => a.Id).Take(filter.Limit)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<V2ActivityRecord>> ListActivityAsync(V2ActivityFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        var logs = db.ActivityLogs.AsNoTracking();
        if (filter.From is { } from)
        {
            logs = logs.Where(a => a.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            logs = logs.Where(a => a.OccurredAt < to);
        }

        if (filter.ActorId is { } actor)
        {
            logs = logs.Where(a => a.ActorId == actor);
        }

        if (filter.ProductId is { } product)
        {
            var subject = SubjectPattern("product", product);
            logs = logs.Where(a => a.Subjects.Contains(subject));
        }

        if (filter.BinId is { } bin)
        {
            var subject = SubjectPattern("bin", bin);
            logs = logs.Where(a => a.Subjects.Contains(subject));
        }

        if (filter.Type is { } type)
        {
            logs = logs.Where(a => a.Type == type);
        }

        if (filter.Success is { } success)
        {
            logs = logs.Where(a => a.Success == success);
        }

        if (filter.BeforeId is { } before)
        {
            logs = logs.Where(a => a.Id < before);
        }

        var rows = await logs.OrderByDescending(a => a.Id).Take(filter.Limit).ToListAsync(cancellationToken);
        return rows.Select(ToActivity).ToList();
    }

    public async Task<V2DashboardSnapshot> GetDashboardAsync(long? actorId, int take, CancellationToken cancellationToken)
    {
        // Several SELECTs: one deferred (read-only, no writer lock) transaction makes them a single SQLite snapshot.
        await using var db = await Contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var snapshot = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await db.Database.UseTransactionAsync(snapshot, cancellationToken);

        var skus = await db.Products.CountAsync(p => !p.IsArchived, cancellationToken);
        var openAlerts = await db.Alerts.CountAsync(a => a.Status == "open", cancellationToken);
        var active = await db.CountTasks.CountAsync(t => OpenCountStatuses.Contains(t.Status), cancellationToken);
        var unresolved = await db.CountTasks.CountAsync(t => t.HasUnresolvedInvestigation, cancellationToken);
        var logs = db.ActivityLogs.AsNoTracking();
        if (actorId is { } actor)
        {
            logs = logs.Where(a => a.ActorId == actor);
        }

        var recent = (await logs.OrderByDescending(a => a.Id).Take(take).ToListAsync(cancellationToken)).Select(ToActivity).ToList();
        var alerts = await ProjectAlerts(db, db.Alerts.AsNoTracking().Where(a => a.Status == "open").OrderByDescending(a => a.Id).Take(take))
            .ToListAsync(cancellationToken);
        return new V2DashboardSnapshot(skus, openAlerts, active, unresolved, recent, alerts);
    }

    private static string SubjectPattern(string type, long id) =>
        $"\"type\":\"{type}\",\"id\":\"{id.ToString(CultureInfo.InvariantCulture)}\"";

    private static IQueryable<V2AlertView> ProjectAlerts(WmsV2DbContext db, IQueryable<V2AlertRow> alerts) =>
        alerts.Select(a => new V2AlertView(
            a.Id, a.Type, a.Status, a.BinId,
            db.Bins.Where(b => b.Id == a.BinId).Select(b => b.Code).FirstOrDefault() ?? string.Empty,
            a.ProductId,
            db.Products.Where(p => p.Id == a.ProductId).Select(p => p.Name).FirstOrDefault() ?? string.Empty,
            a.Message, a.CreatedAt, a.TriggerSnapshot, a.CountTaskId, a.CountResultId, a.ResolvedAt, a.ResolutionNote));

    private static V2ActivityRecord ToActivity(V2ActivityLogRow row) => new(
        row.Id, row.OccurredAt, row.Type, row.Description, row.Success,
        JsonSerializer.Deserialize<V2LoggedSubject[]>(row.Subjects, Json) ?? [],
        row.ActorId, row.ActorName, row.ActorRole, row.LoginName, row.FailureCode, row.FailureReason,
        string.IsNullOrEmpty(row.Changes) ? null : JsonSerializer.Deserialize<V2FieldChange[]>(row.Changes, Json));
}
