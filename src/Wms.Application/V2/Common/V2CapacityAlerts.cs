using System.Text.Json;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Common;

/// <summary>
/// Capacity alerts (decisions §7, §17.5): one capacity state per bin, priority over capacity &gt; high &gt; low &gt;
/// normal, strict comparisons; a bin without product or capacity raises nothing; a zero-stock bin that kept its product
/// and capacity is at 0 % (low when the low threshold is above 0). A continuing state keeps its alert and its original
/// trigger snapshot; a changed state resolves the old alert and opens one of the new type; back to normal resolves it.
/// Count discrepancy alerts are never touched here.
/// </summary>
public static class V2CapacityAlerts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string? AlertTypeFor(string capacityStatus) => capacityStatus switch
    {
        CapacityStatuses.Low => AlertTypesV2.LowStock,
        CapacityStatuses.High => AlertTypesV2.HighStock,
        CapacityStatuses.OverCapacity => AlertTypesV2.OverCapacity,
        _ => null,
    };

    /// <summary>
    /// Re-evaluates every non-archived bin against the given default thresholds, inside the caller's transaction.
    /// <see cref="V2CapacityAlertChanges.Subjects"/> names, for every bin whose alert opened or closed, the alert(s), the
    /// bin and its product, so the caller's log entry is found from any of them. Bins whose state continues are not
    /// touched and not named.
    /// </summary>
    public static Task<V2CapacityAlertChanges> ReconcileAsync(
        IV2CatalogWriteScope scope, long defaultLowMicros, long defaultHighMicros, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        ReconcileAsync(scope, null, defaultLowMicros, defaultHighMicros, now, reason, cancellationToken);

    /// <summary>The same rule for the given bins only (those an inventory transaction changed), in the same transaction.</summary>
    public static async Task<V2CapacityAlertChanges> ReconcileAsync(
        IV2CatalogWriteScope scope,
        IReadOnlyCollection<long>? binIds,
        long defaultLowMicros,
        long defaultHighMicros,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var opened = new List<long>();
        var resolved = new List<long>();
        var subjects = new List<V2SubjectRecord>();
        var states = binIds is null
            ? await scope.ListCapacityStatesAsync(cancellationToken)
            : await scope.ListCapacityStatesAsync(binIds, cancellationToken);
        foreach (var bin in states)
        {
            var low = bin.LowThresholdMicros ?? defaultLowMicros;
            var high = bin.HighThresholdMicros ?? defaultHighMicros;
            var status = bin.ProductId is null
                ? CapacityStatuses.NotConfigured
                : V2Quantities.Status(bin.QuantityMicros, bin.CapacityMicros, low, high);
            var desired = AlertTypeFor(status);
            if (string.Equals(bin.OpenAlertType, desired, StringComparison.Ordinal))
            {
                continue; // continuing state (or none): no new record, original snapshot untouched
            }

            if (bin.OpenAlertId is { } open)
            {
                await scope.ResolveAlertAsync(
                    open, now, desired is null ? $"恢复正常（{reason}）" : $"状态变为 {desired}（{reason}）", cancellationToken);
                resolved.Add(open);
                subjects.Add(new V2SubjectRecord(SubjectTypes.Alert, open));
            }

            subjects.Add(new V2SubjectRecord(SubjectTypes.Bin, bin.BinId, bin.Code));
            if (bin.ProductId is { } product)
            {
                subjects.Add(new V2SubjectRecord(SubjectTypes.Product, product, bin.Sku));
            }

            if (desired is not null)
            {
                var occupancy = V2Quantities.Occupancy(bin.QuantityMicros, bin.CapacityMicros) ?? "0";
                var snapshot = JsonSerializer.Serialize(
                    new
                    {
                        quantity = WireQuantity.Format(bin.QuantityMicros),
                        capacity = WireQuantity.Format(bin.CapacityMicros!.Value),
                        occupancyPercent = occupancy,
                        lowThreshold = WireQuantity.Format(low),
                        highThreshold = WireQuantity.Format(high),
                        unitName = bin.UnitName,
                        decimalPlaces = bin.DecimalPlaces,
                        sku = bin.Sku,
                    },
                    Json);
                var message = desired switch
                {
                    AlertTypesV2.LowStock => $"{bin.Code} 占用 {occupancy}%，低于 {WireQuantity.Format(low)}%",
                    AlertTypesV2.HighStock => $"{bin.Code} 占用 {occupancy}%，高于 {WireQuantity.Format(high)}%",
                    _ => $"{bin.Code} 占用 {occupancy}%，超过容量",
                };
                var id = await scope.InsertAlertAsync(new V2NewAlert(desired, bin.BinId, bin.ProductId!.Value, message, snapshot, now), cancellationToken);
                opened.Add(id);
                subjects.Add(new V2SubjectRecord(SubjectTypes.Alert, id));
            }
        }

        return new V2CapacityAlertChanges(opened, resolved, subjects.Distinct().ToList());
    }
}

/// <summary>Alerts opened and resolved by a re-evaluation, and every alert, bin and product concerned (distinct).</summary>
public sealed record V2CapacityAlertChanges(IReadOnlyList<long> Opened, IReadOnlyList<long> Resolved, IReadOnlyList<V2SubjectRecord> Subjects);
