using Wms.Application.V2.Admin;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Counts;
using Wms.Application.V2.Inventory;
using Wms.Application.V2.Monitoring;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B5a alerts, activity log and dashboard over real persisted events on a temporary v2 file (SQLite evidence only).</summary>
public sealed class V2MonitoringServiceTests : V2ServiceTestBase
{
    private V2MonitoringService Monitoring => Get<V2MonitoringService>();

    [Fact]
    public async Task Alerts_keep_their_trigger_snapshot_and_filter_and_page_exactly()
    {
        var admin = await SignInAsync("admin");
        var (bin, product) = await StockedBinAsync(admin, "A", "Bolt", "2");
        Clock.Advance(TimeSpan.FromMinutes(1));
        Ok(await Receive(admin, product, bin, "90")); // low -> high: the low alert closes, a high one opens

        var all = Ok(await Monitoring.ListAlertsAsync(null, null, null, null, null, null, default));
        Assert.Equal(new[] { (AlertTypesV2.HighStock, AlertStatusesV2.Open), (AlertTypesV2.LowStock, AlertStatusesV2.Resolved) }, all.Items.Select(a => (a.Type, a.Status)));
        var low = all.Items[1];
        Assert.Equal(("2", "100", "2", "20", "80", 0), (low.Trigger.Quantity, low.Trigger.Capacity, low.Trigger.OccupancyPercent, low.Trigger.LowThreshold, low.Trigger.HighThreshold, low.Trigger.DecimalPlaces));
        Assert.NotNull(low.ResolvedAt);
        Assert.Equal(("92", bin, product), (all.Items[0].Trigger.Quantity, low.BinId, low.ProductId));

        Assert.Equal(AlertTypesV2.HighStock, Assert.Single(Ok(await Monitoring.ListAlertsAsync("open", null, null, null, null, null, default)).Items).Type);
        Assert.Equal(AlertStatusesV2.Resolved, Assert.Single(Ok(await Monitoring.ListAlertsAsync(null, "lowStock", null, product, null, null, default)).Items).Status);
        Assert.Empty(Ok(await Monitoring.ListAlertsAsync(null, null, "999", null, null, null, default)).Items);
        var first = Ok(await Monitoring.ListAlertsAsync(null, null, bin, null, null, 1, default));
        var second = Ok(await Monitoring.ListAlertsAsync(null, null, bin, null, first.NextCursor, 1, default));
        Assert.Equal((all.Items[0].Id, all.Items[1].Id), (Assert.Single(first.Items).Id, Assert.Single(second.Items).Id));
        Assert.Null(second.NextCursor);

        foreach (var bad in new[]
        {
            await Monitoring.ListAlertsAsync("closed", null, null, null, null, null, default),
            await Monitoring.ListAlertsAsync(null, "Low", null, null, null, null, default),
            await Monitoring.ListAlertsAsync(null, null, "abc", null, null, null, default),
            await Monitoring.ListAlertsAsync(null, null, null, null, "!!", null, default),
            await Monitoring.ListAlertsAsync(null, null, null, null, null, 0, default),
        })
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(bad).Code);
        }

        // The trigger is never rewritten after opening (database guard).
        Assert.ThrowsAny<Exception>(() => Execute($"UPDATE Alerts SET TriggerSnapshot = '{{}}' WHERE Id = {low.Id}"));
    }

    [Fact]
    public async Task Activity_filters_search_before_the_page_and_utc_bounds()
    {
        var admin = await SignInAsync("admin");
        var receiver = await SignInAsync("receiver");
        Clock.Advance(TimeSpan.FromHours(1));
        var (bin, product) = await StockedBinAsync(admin, "A", "Äpfel", "5");
        var failed = await Get<V2ProductService>().CreateAsync(receiver, new ProductCreateRequest("Nope", "1", 0, false, []), Guid.NewGuid().ToString(), default);
        Assert.False(failed.IsSuccess);
        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await CreateProductAsync(admin, $"Other {i}");
        }

        var found = Ok(await Monitoring.ListActivityAsync("äPFEL", null, null, null, null, null, ActivityTypes.ProductCreate, null, null, 1, default));
        var created = Assert.Single(found.Items);
        Assert.Contains("Äpfel", created.Description, StringComparison.Ordinal);
        Assert.Contains(created.Subjects, s => s.Type == SubjectTypes.Product && s.Id == product);
        Assert.Contains(created.Changes!, c => c.Field == "name" && c.After == "Äpfel");
        Assert.Equal(("系统管理员", "systemAdministrator"), (created.ActorName, created.ActorRole));

        var byProduct = Ok(await Monitoring.ListActivityAsync(null, null, null, null, product, null, null, null, null, null, default)).Items;
        Assert.Equal(new[] { ActivityTypes.Receipt, ActivityTypes.ProductCreate }, byProduct.Select(a => a.Type));
        Assert.Contains(Ok(await Monitoring.ListActivityAsync(null, null, null, null, null, bin, null, null, null, null, default)).Items, a => a.Type == ActivityTypes.BinCreate);
        var failures = Ok(await Monitoring.ListActivityAsync(null, null, null, null, null, null, null, false, null, null, default)).Items;
        Assert.Equal((ProblemCodesV2.Forbidden, ActivityTypes.ProductCreate), (Assert.Single(failures).FailureCode, failures[0].Type));
        var receiverId = failures[0].ActorId!;
        Assert.NotEqual(created.ActorId, receiverId);
        Assert.All(Ok(await Monitoring.ListActivityAsync(null, null, null, receiverId, null, null, null, null, null, null, default)).Items, a => Assert.Equal(receiverId, a.ActorId));

        // from inclusive / to exclusive, any offset: 10:00+01:00 is 09:00Z, the time of both logins.
        var logins = Ok(await Monitoring.ListActivityAsync(null, "2026-10-20T10:00:00+01:00", "2026-10-20T09:00:00.0000001Z", null, null, null, null, null, null, null, default)).Items;
        Assert.Equal(2, logins.Count);
        Assert.All(logins, a => Assert.Equal(ActivityTypes.Login, a.Type));
        Assert.Empty(Ok(await Monitoring.ListActivityAsync(null, "2026-10-20T09:00:00Z", "2026-10-20T09:00:00Z", null, null, null, null, null, null, null, default)).Items);
        Assert.Empty(Ok(await Monitoring.ListActivityAsync(null, "2026-10-20T09:00:00Z", "2026-10-20T10:00:00+01:00", null, null, null, null, null, null, null, default)).Items); // same instant

        var page1 = Ok(await Monitoring.ListActivityAsync(null, null, null, null, null, null, ActivityTypes.ProductCreate, true, null, 2, default));
        var page2 = Ok(await Monitoring.ListActivityAsync(null, null, null, null, null, null, ActivityTypes.ProductCreate, true, page1.NextCursor, 10, default));
        Assert.Equal(6, page1.Items.Count + page2.Items.Count);
        Assert.Empty(page1.Items.Select(a => a.Id).Intersect(page2.Items.Select(a => a.Id)));

        foreach (var bad in new[]
        {
            await Monitoring.ListActivityAsync(null, "yesterday", null, null, null, null, null, null, null, null, default),
            await Monitoring.ListActivityAsync(null, null, "2026-13-01", null, null, null, null, null, null, null, default),
            await Monitoring.ListActivityAsync(null, null, null, "x", null, null, null, null, null, null, default),
            await Monitoring.ListActivityAsync(null, null, null, null, "-1", null, null, null, null, null, default),
            await Monitoring.ListActivityAsync(null, null, null, null, null, null, "productDelete", null, null, null, default),
            await Monitoring.ListActivityAsync(null, "2026-10-20T09:00:00.0000001Z", "2026-10-20T09:00:00Z", null, null, null, null, null, null, null, default),
            await Monitoring.ListActivityAsync(null, "2026-10-20T10:00:00Z", "2026-10-20T10:30:00+01:00", null, null, null, null, null, null, null, default),
        })
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(bad).Code);
        }
    }

    [Fact]
    public async Task Dashboard_counts_and_scope_only_changes_recent_activity()
    {
        var admin = await SignInAsync("admin");
        var (a, _) = await StockedBinAsync(admin, "A", "Bolt", "5");
        await CreateProductAsync(admin, "Zero stock");
        var archived = await CreateProductAsync(admin, "Old");
        Ok(await Get<V2ProductService>().ArchiveAsync(admin, archived.Id, new DeleteRequest(archived.Version), default));

        // A serial investigation (unresolved, one discrepancy alert) and an active count.
        var scanner = Ok(await Get<V2ProductService>().CreateAsync(admin, new ProductCreateRequest("Scanner", "1", 0, true, []), Guid.NewGuid().ToString(), default)).Id;
        var s = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest("S", "R1", "01", "01"), default)).Id;
        Ok(await Get<V2InventoryService>().ExecuteAsync(
            TransactionTypesV2.Receipt, admin, new InventoryOperationRequest([new InventoryLine(scanner, ToBinId: s, Capacity: "10", SerialNumbers: ["S-1", "S-2"])]),
            Guid.NewGuid().ToString(), default));
        var counts = Get<V2CountService>();
        var task = Ok(await counts.StartAsync(admin, new CountStartRequest(s), Guid.NewGuid().ToString(), default));
        task = Ok(await counts.SubmitAsync(admin, task.Id, new CountSubmitRequest("1", 1, 1), default));
        Ok(await counts.ConfirmAsync(admin, task.Id, new CountConfirmRequest(task.LatestResultId!, 1, task.Version), Guid.NewGuid().ToString(), default));
        Ok(await counts.StartAsync(admin, new CountStartRequest(a), Guid.NewGuid().ToString(), default));

        var supervisor = await SignInAsync("supervisor");
        var all = Ok(await Monitoring.GetDashboardAsync(supervisor, null, default));
        var mine = Ok(await Monitoring.GetDashboardAsync(supervisor, DashboardScopes.Mine, default));
        Assert.Equal((3, 1, 1), (all.SkuCount, all.ActiveCountCount, all.UnresolvedInvestigationCount));
        Assert.Equal(Scalar("SELECT COUNT(*) FROM Alerts WHERE Status = 'open'"), all.OpenAlertCount);
        Assert.Contains(all.Alerts, x => x.Type == AlertTypesV2.CountDiscrepancy && x.Trigger.Round == 1 && x.Trigger.Difference == "-1");
        Assert.All(all.Alerts, x => Assert.Equal(AlertStatusesV2.Open, x.Status));
        Assert.Equal(DashboardScopes.All, all.Scope);
        Assert.Equal(10, all.RecentActivity.Count);
        Assert.Equal(all.RecentActivity.OrderByDescending(x => long.Parse(x.Id, System.Globalization.CultureInfo.InvariantCulture)).Select(x => x.Id), all.RecentActivity.Select(x => x.Id));

        Assert.Equal((all.SkuCount, all.OpenAlertCount, all.ActiveCountCount, all.UnresolvedInvestigationCount), (mine.SkuCount, mine.OpenAlertCount, mine.ActiveCountCount, mine.UnresolvedInvestigationCount));
        Assert.Equal(all.Alerts.Select(x => x.Id), mine.Alerts.Select(x => x.Id));
        Assert.Equal(ActivityTypes.Login, Assert.Single(mine.RecentActivity).Type);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Monitoring.GetDashboardAsync(supervisor, "everyone", default)).Code);
    }

    [Fact]
    public async Task Dashboard_reads_during_concurrent_commands_are_one_consistent_snapshot()
    {
        // Timing-based: start and cancel each commit the task change and its log entry atomically; a reader that mixed
        // two commits would pair the latest entry with the other active-count value.
        var admin = await SignInAsync("admin");
        var (bin, _) = await StockedBinAsync(admin, "A", "Bolt", "5");
        var counts = Get<V2CountService>();
        using var done = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    var task = Ok(await counts.StartAsync(admin, new CountStartRequest(bin), Guid.NewGuid().ToString(), default));
                    Ok(await counts.CancelAsync(admin, task.Id, new CountCancelRequest("again", 1, task.Version), default));
                }
            }
            finally
            {
                await done.CancelAsync();
            }
        });

        var reads = 0;
        while (!done.IsCancellationRequested || reads == 0)
        {
            var seen = Ok(await Monitoring.GetDashboardAsync(admin, null, default));
            reads++;
            var latest = seen.RecentActivity[0].Type;
            if (latest == ActivityTypes.CountStart)
            {
                Assert.Equal(1, seen.ActiveCountCount);
            }
            else if (latest == ActivityTypes.CountCancel)
            {
                Assert.Equal(0, seen.ActiveCountCount);
            }
        }

        await writer;
        Assert.Equal(0, Ok(await Monitoring.GetDashboardAsync(admin, null, default)).ActiveCountCount);
    }

    private Task<Wms.Application.V2.Common.V2Result<InventoryTransactionResponse>> Receive(V2SessionPrincipal who, string product, string bin, string quantity) =>
        Get<V2InventoryService>().ExecuteAsync(
            TransactionTypesV2.Receipt, who, new InventoryOperationRequest([new InventoryLine(product, ToBinId: bin, Quantity: quantity)]), Guid.NewGuid().ToString(), default);

    private async Task<(string Bin, string Product)> StockedBinAsync(V2SessionPrincipal admin, string zone, string name, string quantity)
    {
        var product = await CreateProductAsync(admin, name);
        var bin = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest(zone, "R1", "01", "01"), default)).Id;
        Ok(await Get<V2InventoryService>().ExecuteAsync(
            TransactionTypesV2.Receipt, admin, new InventoryOperationRequest([new InventoryLine(product.Id, ToBinId: bin, Quantity: quantity, Capacity: "100")]),
            Guid.NewGuid().ToString(), default));
        return (bin, product.Id);
    }
}
