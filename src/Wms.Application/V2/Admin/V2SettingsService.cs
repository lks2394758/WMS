using System.Globalization;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Admin;

/// <summary>
/// Warehouse settings (decisions §12 SystemSettings, §13, §17.5-17.6): read by any signed-in user, changed only by a
/// session whose current role is <c>systemAdministrator</c>, with the stored version.
/// <list type="bullet">
/// <item>rack defaults 1-100 (affect only racks generated later); session hours 1-168 (only new logins; existing sessions
/// keep their expiry); thresholds percentages with at most 2 decimals, 0 ≤ low &lt; high ≤ 100;</item>
/// <item>new default thresholds are checked in the same transaction against every bin's own overrides: if any bin's
/// effective low would not be below its effective high, the whole change is rejected (<c>THRESHOLD_CONFLICT</c>, bins in
/// <c>subjects</c>); no override is dropped and nothing is half saved;</item>
/// <item>after a threshold change every bin's capacity alert is re-evaluated at once (<see cref="V2CapacityAlerts"/>).</item>
/// </list>
/// </summary>
public sealed class V2SettingsService(IV2AdminReader reader, V2CatalogWriter writer)
{
    private const long SettingsId = 1;
    private const int MaxConflictSubjects = 50;

    public async Task<V2Result<SettingsResponse>> GetAsync(CancellationToken cancellationToken) =>
        ToResponse(await reader.GetSettingsAsync(cancellationToken));

    public async Task<V2Result<SettingsResponse>> UpdateAsync(
        V2SessionPrincipal principal, SettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subjects = new[] { new V2SubjectRecord(SubjectTypes.Settings, SettingsId) };
        var rejected = Validate(request, out var low, out var high);
        if (rejected is not null)
        {
            return await writer.RejectAsync<SettingsResponse>(
                principal, PermissionNames.SettingsManage, ActivityTypes.SettingsUpdate, "修改系统设置", subjects, rejected, cancellationToken);
        }

        var subject = V2CatalogMapping.Ref(SubjectTypes.Settings, SettingsId);
        return await writer.RunAsync<SettingsResponse>(
            principal, PermissionNames.SettingsManage, ActivityTypes.SettingsUpdate, "修改系统设置", subjects,
            async (scope, actor, now) =>
            {
                var current = await scope.GetSettingsAsync(cancellationToken);
                if (current.Version != request.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                var bins = await scope.ListCapacityStatesAsync(cancellationToken);
                var conflicts = bins
                    .Where(b => b.LowThresholdMicros is not null || b.HighThresholdMicros is not null)
                    .Where(b => (b.LowThresholdMicros ?? low) >= (b.HighThresholdMicros ?? high))
                    .ToList();
                if (conflicts.Count > 0)
                {
                    return new V2Failure(
                        ProblemCodesV2.ThresholdConflict,
                        $"{conflicts.Count} bin(s) have their own threshold that would not stay below/above the new default; nothing was saved.",
                        Subjects: conflicts.Take(MaxConflictSubjects).Select(b => V2CatalogMapping.Ref(SubjectTypes.Bin, b.BinId, b.Code)).ToList());
                }

                if (!await scope.UpdateSettingsAsync(
                        new V2SettingsUpdate(
                            current.Version, request.DefaultRackLevels, request.DefaultCellsPerLevel, low, high, request.SessionHours, actor.UserId, now),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var changes = new List<V2FieldChange>();
                Add(changes, "defaultRackLevels", current.DefaultRackLevels, request.DefaultRackLevels);
                Add(changes, "defaultCellsPerLevel", current.DefaultCellsPerLevel, request.DefaultCellsPerLevel);
                AddPercent(changes, "lowThreshold", current.LowThresholdMicros, low);
                AddPercent(changes, "highThreshold", current.HighThresholdMicros, high);
                Add(changes, "sessionHours", current.SessionHours, request.SessionHours);

                var logSubjects = new List<V2SubjectRecord>(subjects);
                if (low != current.LowThresholdMicros || high != current.HighThresholdMicros)
                {
                    var alerts = await V2CapacityAlerts.ReconcileAsync(scope, low, high, now, "默认阈值已修改", cancellationToken);
                    logSubjects.AddRange(alerts.Subjects); // the alerts plus the bins and products whose alert changed
                    if (alerts.Opened.Count + alerts.Resolved.Count > 0)
                    {
                        changes.Add(new V2FieldChange("capacityAlerts", $"{alerts.Resolved.Count} resolved", $"{alerts.Opened.Count} opened"));
                    }
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.SettingsUpdate, "修改系统设置", true, logSubjects, actor.UserId, actor.DisplayName, actor.Role,
                    Changes: changes));
                return ToResponse(await scope.GetSettingsAsync(cancellationToken));
            },
            cancellationToken);
    }

    public static SettingsResponse ToResponse(V2SettingsRecord settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsResponse(
            settings.DefaultRackLevels,
            settings.DefaultCellsPerLevel,
            WireQuantity.Format(settings.LowThresholdMicros),
            WireQuantity.Format(settings.HighThresholdMicros),
            settings.SessionHours,
            settings.Version,
            settings.UpdatedAt,
            settings.UpdatedById is { } by ? WireId.Format(by) : null,
            settings.UpdatedByName);
    }

    private static V2Failure? Validate(SettingsUpdateRequest request, out long low, out long high)
    {
        high = 0;
        if (request.DefaultRackLevels is < 1 or > 100)
        {
            low = 0;
            return V2Failure.Validation("defaultRackLevels", "defaultRackLevels must be 1-100.");
        }

        if (request.DefaultCellsPerLevel is < 1 or > 100)
        {
            low = 0;
            return V2Failure.Validation("defaultCellsPerLevel", "defaultCellsPerLevel must be 1-100.");
        }

        if (request.SessionHours is < 1 or > 168)
        {
            low = 0;
            return V2Failure.Validation("sessionHours", "sessionHours must be 1-168.");
        }

        if (!WirePercent.TryParse(request.LowThreshold, out low))
        {
            return V2Failure.Validation("lowThreshold", "lowThreshold must be a percentage 0-100 with at most 2 decimals.");
        }

        if (!WirePercent.TryParse(request.HighThreshold, out high))
        {
            return V2Failure.Validation("highThreshold", "highThreshold must be a percentage 0-100 with at most 2 decimals.");
        }

        return low < high ? null : V2Failure.Validation("lowThreshold", "lowThreshold must be below highThreshold.");
    }

    private static void Add(List<V2FieldChange> changes, string field, int before, int after)
    {
        if (before != after)
        {
            changes.Add(new V2FieldChange(field, before.ToString(CultureInfo.InvariantCulture), after.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AddPercent(List<V2FieldChange> changes, string field, long before, long after)
    {
        if (before != after)
        {
            changes.Add(new V2FieldChange(field, WireQuantity.Format(before), WireQuantity.Format(after)));
        }
    }
}
