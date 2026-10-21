using System.Globalization;
using System.Text.Json;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Monitoring;

/// <summary>
/// Alerts, activity log and dashboard (contracts/v2/README.md §2, §10). Read-only for any signed-in role.
/// <list type="bullet">
/// <item>Enum, ID and time parameters are validated (<c>VALIDATION_FAILED</c>); <c>from</c> inclusive, <c>to</c> exclusive,
/// any ISO 8601 offset, compared in UTC.</item>
/// <item>Exact filters run in SQL; activity <c>search</c> (Unicode invariant upper case) matches description, actor
/// name, login name, failure reason and subject labels and is applied before the page is cut.</item>
/// <item>Alert triggers, log actors, subjects and changes are returned as written then; only alert bin code and
/// product name are current labels (the alert row stores IDs).</item>
/// <item>Dashboard: one consistent snapshot; <c>scope</c> affects only <c>recentActivity</c>.</item>
/// </list>
/// </summary>
public sealed class V2MonitoringService(IV2MonitoringReader reader)
{
    public const int DashboardTake = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<V2Result<Page<AlertResponse>>> ListAlertsAsync(
        string? status, string? type, string? binId, string? productId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var before, out var size) is { } badPage)
        {
            return badPage;
        }

        if (status is not null && !AlertStatusesV2.All.Contains(status, StringComparer.Ordinal))
        {
            return V2Failure.Validation("status", "status must be open or resolved.");
        }

        if (type is not null && !AlertTypesV2.All.Contains(type, StringComparer.Ordinal))
        {
            return V2Failure.Validation("type", "Unknown alert type.");
        }

        var badBin = OptionalId(binId, "binId", out var bin);
        var badProduct = OptionalId(productId, "productId", out var product);
        if ((badBin ?? badProduct) is { } invalid)
        {
            return invalid;
        }

        var rows = await reader.ListAlertsAsync(new V2AlertFilter(status, type, bin, product, before, size + 1), cancellationToken);
        return new Page<AlertResponse>(
            rows.Take(size).Select(ToAlert).ToList(),
            rows.Count > size ? V2Paging.Encode(rows[size - 1].Id) : null);
    }

    public async Task<V2Result<Page<ActivityLogResponse>>> ListActivityAsync(
        string? search, string? from, string? to, string? actorId, string? productId, string? binId, string? type, bool? success,
        string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var before, out var size) is { } badPage)
        {
            return badPage;
        }

        if (type is not null && !ActivityTypes.All.Contains(type, StringComparer.Ordinal))
        {
            return V2Failure.Validation("type", "Unknown activity type.");
        }

        var badFrom = Time(from, "from", out var fromTime);
        var badTo = Time(to, "to", out var toTime);
        var badActor = OptionalId(actorId, "actorId", out var actor);
        var badProduct = OptionalId(productId, "productId", out var product);
        var badBin = OptionalId(binId, "binId", out var bin);
        if ((badFrom ?? badTo ?? badActor ?? badProduct ?? badBin) is { } invalid)
        {
            return invalid;
        }

        if (fromTime > toTime)
        {
            return V2Failure.Validation("to", "to must not be earlier than from.");
        }

        var text = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var batch = text is null ? size + 1 : Math.Max(100, (size + 1) * 2);
        var page = new List<V2ActivityRecord>(size + 1);
        var cursorId = before;
        while (page.Count <= size)
        {
            var rows = await reader.ListActivityAsync(
                new V2ActivityFilter(fromTime, toTime, actor, product, bin, type, success, cursorId, batch), cancellationToken);
            foreach (var row in rows)
            {
                if (text is null || Matches(row, text))
                {
                    page.Add(row);
                    if (page.Count > size)
                    {
                        break;
                    }
                }
            }

            if (rows.Count < batch)
            {
                break;
            }

            cursorId = rows[^1].Id;
        }

        return new Page<ActivityLogResponse>(
            page.Take(size).Select(ToActivity).ToList(),
            page.Count > size ? V2Paging.Encode(page[size - 1].Id) : null);
    }

    public async Task<V2Result<DashboardResponse>> GetDashboardAsync(V2SessionPrincipal principal, string? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var effective = string.IsNullOrEmpty(scope) ? DashboardScopes.All : scope;
        if (effective is not (DashboardScopes.All or DashboardScopes.Mine))
        {
            return V2Failure.Validation("scope", "scope must be all or mine.");
        }

        var snapshot = await reader.GetDashboardAsync(
            effective == DashboardScopes.Mine ? principal.UserId : null, DashboardTake, cancellationToken);
        return new DashboardResponse(
            snapshot.SkuCount, snapshot.OpenAlertCount, snapshot.ActiveCountCount, snapshot.UnresolvedInvestigationCount, effective,
            snapshot.RecentActivity.Select(ToActivity).ToList(),
            snapshot.OpenAlerts.Select(ToAlert).ToList());
    }

    private static bool Matches(V2ActivityRecord row, string text) =>
        new[] { row.Description, row.ActorName, row.LoginName, row.FailureReason }
            .Concat(row.Subjects.Select(s => s.Label))
            .Any(value => value is not null && V2Text.Normalize(value).Contains(text, StringComparison.Ordinal));

    private static AlertResponse ToAlert(V2AlertView a) => new(
        WireId.Format(a.Id), a.Type, a.Status, WireId.Format(a.BinId), a.BinCode, WireId.Format(a.ProductId), a.ProductName, a.Message,
        a.CreatedAt,
        JsonSerializer.Deserialize<AlertSnapshot>(a.TriggerJson, Json) ?? new AlertSnapshot(),
        a.CountTaskId is { } task ? WireId.Format(task) : null,
        a.CountResultId is { } result ? WireId.Format(result) : null,
        a.ResolvedAt,
        a.ResolutionNote);

    private static ActivityLogResponse ToActivity(V2ActivityRecord r) => new(
        WireId.Format(r.Id), r.OccurredAt, r.Type, r.Description, r.Success,
        r.Subjects.Select(s => new SubjectRef(s.Type, s.Id, s.Label)).ToList(),
        r.ActorId is { } actor ? WireId.Format(actor) : null,
        r.ActorName, r.ActorRole, r.LoginName, r.FailureCode, r.FailureReason,
        r.Changes?.Select(c => new FieldChange(c.Field, c.Before, c.After)).ToList());

    private static V2Failure? Time(string? value, string field, out DateTimeOffset? time)
    {
        time = null;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return V2Failure.Validation(field, $"{field} must be an ISO 8601 time.");
        }

        time = parsed;
        return null;
    }

    private static V2Failure? OptionalId(string? value, string field, out long? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (V2Text.Id(value, field, out var parsed) is { } bad)
        {
            return bad;
        }

        id = parsed;
        return null;
    }
}
