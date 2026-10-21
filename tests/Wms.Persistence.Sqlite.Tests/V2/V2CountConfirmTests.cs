using Wms.Application.V2.Admin;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Application.V2.Counts;
using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B4b count confirm and resolve through the real services on a temporary v2 file (SQLite evidence only).</summary>
public sealed class V2CountConfirmTests : V2ServiceTestBase
{
    private V2CountService Counts => Get<V2CountService>();

    [Fact]
    public async Task Zero_difference_completes_without_a_transaction_and_replays_without_effects()
    {
        var admin = await SignInAsync("admin");
        var (bin, _) = await StockedBinAsync(admin, "5");
        var task = Ok(await Submit(admin, Ok(await Start(admin, bin)), "5"));
        var transactions = Scalar("SELECT COUNT(*) FROM InventoryTransactions");
        var key = Guid.NewGuid().ToString();
        var request = new CountConfirmRequest(task.LatestResultId!, task.Round, task.Version);

        var done = Ok(await Counts.ConfirmAsync(admin, task.Id, request, key, default));
        Assert.Equal((CountStatuses.Completed, false), (done.Status, done.HasUnresolvedInvestigation));
        Assert.Equal(CountResultDispositions.ConfirmedNoDifference, Assert.Single(done.Results).Disposition);
        Assert.Empty(done.AllowedActions);
        Assert.Equal(transactions, Scalar("SELECT COUNT(*) FROM InventoryTransactions"));
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND FrozenCountTaskId IS NOT NULL"));

        var replay = await Counts.ConfirmAsync(admin, task.Id, request, key, default);
        Assert.True(replay.Replayed);
        Assert.Equal(done.Version, replay.Value.Version);
        Assert.Equal(ProblemCodesV2.IdempotencyConflict, Fail(await Counts.ConfirmAsync(admin, task.Id, request with { Reason = "other" }, key, default)).Code);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Counts.ConfirmAsync(admin, task.Id, request, Guid.NewGuid().ToString(), default)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countConfirm' AND Success = 1"));

        // Replay re-checks the current role first: a receiver session never replays.
        var receiver = await SignInAsync("receiver");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Counts.ConfirmAsync(receiver, task.Id, request, key, default)).Code);
    }

    [Fact]
    public async Task Untracked_difference_adjusts_exactly_once_with_a_linked_ledger_and_capacity_alerts()
    {
        var admin = await SignInAsync("admin");
        var (bin, _) = await StockedBinAsync(admin, "2");
        var task = Ok(await Submit(admin, Ok(await Start(admin, bin)), "90.25"));
        var resultId = task.LatestResultId!;
        var request = new CountConfirmRequest(resultId, task.Round, task.Version, " found pallet ");

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.ConfirmAsync(admin, task.Id, request with { Reason = " " }, Guid.NewGuid().ToString(), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.ConfirmAsync(admin, task.Id, request, null, default)).Code);
        Assert.Equal(CountStatuses.PendingConfirmation, Text($"SELECT Status FROM CountTasks WHERE Id = {task.Id}"));

        var key = Guid.NewGuid().ToString();
        var done = Ok(await Counts.ConfirmAsync(admin, task.Id, request, key, default));
        var result = Assert.Single(done.Results);
        Assert.Equal(CountResultDispositions.Adjusted, result.Disposition);
        Assert.Equal(90_250_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND FrozenCountTaskId IS NOT NULL"));
        Assert.Equal(result.AdjustmentTransactionId, Text($"SELECT Id FROM InventoryTransactions WHERE CountResultId = {resultId} AND Type = 'adjustment'"));
        Assert.Equal("found pallet", Text($"SELECT Reason FROM InventoryTransactions WHERE Id = {result.AdjustmentTransactionId}"));
        Assert.Equal(88_250_000L, Scalar($"SELECT QuantityMicros FROM InventoryTransactionLines WHERE TransactionId = {result.AdjustmentTransactionId}"));
        Assert.Equal("highStock", Text($"SELECT Type FROM Alerts WHERE BinId = {bin} AND Status = 'open'"));

        Assert.True((await Counts.ConfirmAsync(admin, task.Id, request, key, default)).Replayed);
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM InventoryTransactions WHERE CountResultId = {resultId}"));
        Assert.Equal(90_250_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.ThrowsAny<Exception>(() => Execute($"UPDATE InventoryTransactions SET Reason = 'x' WHERE Id = {result.AdjustmentTransactionId}"));
    }

    [Fact]
    public async Task A_failed_confirm_rolls_back_everything_and_parallel_confirms_have_one_winner()
    {
        var admin = await SignInAsync("admin");
        var (bin, _) = await StockedBinAsync(admin, "4");
        var task = Ok(await Submit(admin, Ok(await Start(admin, bin)), "6"));
        var request = new CountConfirmRequest(task.LatestResultId!, task.Round, task.Version, "r");

        // Break the frozen-book premise underneath (raw SQL): the confirm must change nothing.
        Execute($"UPDATE Bins SET QuantityMicros = 4500000 WHERE Id = {bin}");
        Assert.Equal(ProblemCodesV2.ConcurrencyConflict, Fail(await Counts.ConfirmAsync(admin, task.Id, request, Guid.NewGuid().ToString(), default)).Code);
        Assert.Equal((CountStatuses.PendingConfirmation, task.Id), (Text($"SELECT Status FROM CountTasks WHERE Id = {task.Id}"), Text($"SELECT FrozenCountTaskId FROM Bins WHERE Id = {bin}")));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM InventoryTransactions WHERE CountResultId IS NOT NULL"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countConfirm' AND Success = 1"));
        Execute($"UPDATE Bins SET QuantityMicros = 4000000 WHERE Id = {bin}");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Counts.ConfirmAsync(admin, task.Id, request, Guid.NewGuid().ToString(), default))));
        Assert.Single(attempts, a => a.IsSuccess);
        Assert.All(attempts.Where(a => !a.IsSuccess), a => Assert.Equal(ProblemCodesV2.VersionConflict, a.Failure!.Code));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM InventoryTransactions WHERE CountResultId IS NOT NULL"));
        Assert.Equal(6_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
    }

    [Fact]
    public async Task Serial_differences_open_one_alert_survive_recounts_and_resolve_covers_all_unresolved_results()
    {
        var admin = await SignInAsync("admin");
        var (bin, product) = await SerialBinAsync(admin, ["S-1", "S-2"]);
        var items = Text("SELECT group_concat(Id || ':' || ifnull(BinId, '') || ':' || InStock, ',') FROM SerializedItems");

        var task = await ConfirmAsync(admin, Ok(await Submit(admin, Ok(await Start(admin, bin)), "1")));
        var first = task.LatestResultId!;
        Assert.Equal((CountStatuses.Completed, true), (task.Status, task.HasUnresolvedInvestigation));
        Assert.Equal(new[] { CountActions.Recount, CountActions.Resolve }, task.AllowedActions);
        Assert.Equal(CountResultDispositions.UnderInvestigation, Assert.Single(task.Results).Disposition);
        Assert.Equal(2_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.Equal(items, Text("SELECT group_concat(Id || ':' || ifnull(BinId, '') || ':' || InStock, ',') FROM SerializedItems"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM InventoryTransactions WHERE CountResultId IS NOT NULL"));
        Assert.Equal(first, Text($"SELECT CountResultId FROM Alerts WHERE Type = 'countDiscrepancy' AND Status = 'open' AND CountTaskId = {task.Id}"));
        Assert.Equal(ProblemCodesV2.CountTaskExists, Fail(await Start(admin, bin)).Code);

        // Recount from completed, then cancel: completed restored, evidence unchanged.
        var recount = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("again", 1, task.Version), default));
        Assert.Equal(ProblemCodesV2.CountStateConflict, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", true, recount.Version), default)).Code);
        task = Ok(await Counts.CancelAsync(admin, task.Id, new CountCancelRequest("stop", 2, recount.Version), default));
        Assert.Equal((CountStatuses.Completed, true, CountResultDispositions.UnderInvestigation), (task.Status, task.HasUnresolvedInvestigation, task.Results[0].Disposition));

        // Round 3 zero difference: no auto-resolve. Round 4 another difference: still one alert, linked to the first result.
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("again", 2, task.Version), default));
        task = await ConfirmAsync(admin, Ok(await Submit(admin, task, "2")));
        Assert.True(task.HasUnresolvedInvestigation);
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("again", 3, task.Version), default));
        task = await ConfirmAsync(admin, Ok(await Submit(admin, task, "3")));
        Assert.Equal(
            new[] { (1, CountResultDispositions.UnderInvestigation), (3, CountResultDispositions.ConfirmedNoDifference), (4, CountResultDispositions.UnderInvestigation) },
            task.Results.Select(r => (r.Round, r.Disposition)));
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE Type = 'countDiscrepancy' AND CountTaskId = {task.Id}"));
        Assert.Equal(first, Text($"SELECT CountResultId FROM Alerts WHERE Type = 'countDiscrepancy' AND CountTaskId = {task.Id}"));

        var resolved = Ok(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest(" checked shelf ", true, task.Version), default));
        Assert.Equal(new[] { first, task.Results[2].Id }, resolved.ResolvedResultIds);
        Assert.Null(resolved.TransactionId);
        Assert.False(resolved.Task.HasUnresolvedInvestigation);
        Assert.Empty(resolved.Task.AllowedActions);
        Assert.Equal(
            new[] { CountResultDispositions.Resolved, CountResultDispositions.ConfirmedNoDifference, CountResultDispositions.Resolved },
            resolved.Task.Results.Select(r => r.Disposition));
        Assert.All(resolved.Task.Results.Where(r => r.Disposition == CountResultDispositions.Resolved), r => Assert.Equal(resolved.ActivityLogId, r.ResolutionLogId));
        Assert.Equal("resolved", Text($"SELECT Status FROM Alerts WHERE Type = 'countDiscrepancy' AND CountTaskId = {task.Id}"));
        Assert.Contains("checked shelf", Text($"SELECT Changes FROM ActivityLogs WHERE Id = {resolved.ActivityLogId}"), StringComparison.Ordinal);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", true, task.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.CountStateConflict, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", true, resolved.Task.Version), default)).Code);
        Assert.Equal(product, Text($"SELECT ProductId FROM Bins WHERE Id = {bin}"));
    }

    [Fact]
    public async Task Resolve_accepts_only_an_eligible_later_adjustment_and_the_count_role()
    {
        var admin = await SignInAsync("admin");
        var receiver = await SignInAsync("receiver");
        var (bin, product) = await SerialBinAsync(admin, ["S-1", "S-2"]);
        var (other, _) = await StockedBinAsync(admin, "3");
        var before = Ok(await Run(admin, TransactionTypesV2.Adjustment, new InventoryLine(product, ToBinId: bin, Quantity: "1", SerialNumbers: ["S-3"]), "pre")).Id;
        var task = await ConfirmAsync(admin, Ok(await Submit(admin, Ok(await Start(admin, bin)), "2")));
        Assert.True(task.HasUnresolvedInvestigation);
        var receipt = Ok(await Run(admin, TransactionTypesV2.Receipt, new InventoryLine(product, ToBinId: bin, SerialNumbers: ["S-4"]))).Id;
        var otherProduct = Text($"SELECT ProductId FROM Bins WHERE Id = {other}");
        var otherBin = Ok(await Run(admin, TransactionTypesV2.Adjustment, new InventoryLine(otherProduct, ToBinId: other, Quantity: "1"), "o")).Id;

        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Counts.ResolveAsync(receiver, task.Id, new CountResolveRequest("c", true, task.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", false, task.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", true, task.Version, receipt), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest(" ", true, task.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", false, task.Version, "999"), default)).Code);
        foreach (var ineligible in new[] { before, receipt, otherBin })
        {
            Assert.Equal(ProblemCodesV2.TransactionNotEligible, Fail(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("c", false, task.Version, ineligible), default)).Code);
        }

        Clock.Advance(TimeSpan.FromMinutes(5));
        var fix = Ok(await Run(admin, TransactionTypesV2.Adjustment, new InventoryLine(product, ToBinId: bin, Quantity: "-1", SerialNumbers: ["S-4"]), "lost label")).Id;
        var resolved = Ok(await Counts.ResolveAsync(admin, task.Id, new CountResolveRequest("relabelled", false, task.Version, fix), default));
        Assert.Equal(fix, resolved.TransactionId);
        Assert.Equal(CountResultDispositions.Resolved, Assert.Single(resolved.Task.Results).Disposition);
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countResolve' AND Success = 1 AND TransactionId = {fix}"));
        Assert.True(Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countResolve' AND Success = 0") >= 8);
    }

    [Fact]
    public async Task Detail_reads_during_concurrent_commands_are_one_consistent_snapshot()
    {
        var admin = await SignInAsync("admin");
        var (bin, _) = await StockedBinAsync(admin, "5");
        var task = Ok(await Start(admin, bin));
        var id = task.Id;
        using var done = new CancellationTokenSource();

        var writer = Task.Run(async () =>
        {
            try
            {
                var current = task;
                for (var i = 0; i < 15; i++)
                {
                    current = Ok(await Submit(admin, current, "5"));
                    current = Ok(await Counts.RecountAsync(admin, id, new CountRecountRequest("again", current.Round, current.Version), default));
                }

                current = Ok(await Submit(admin, current, "5"));
                await ConfirmAsync(admin, current);
            }
            finally
            {
                await done.CancelAsync();
            }
        });

        var reads = 0;
        while (!done.IsCancellationRequested || reads == 0)
        {
            var seen = Ok(await Counts.GetAsync(admin, id, default));
            reads++;

            // start v1; each submit and each recount adds 1; confirm adds 1 and its evidence.
            switch (seen.Status)
            {
                case CountStatuses.InProgress:
                    Assert.Equal((seen.Round - 1, 2 * seen.Round - 1), (seen.Results.Count, seen.Version));
                    break;
                case CountStatuses.PendingConfirmation:
                    Assert.Equal((seen.Round, 2 * seen.Round), (seen.Results.Count, seen.Version));
                    Assert.Equal(CountResultDispositions.Pending, seen.Results[^1].Disposition);
                    break;
                default:
                    Assert.Equal(CountStatuses.Completed, seen.Status);
                    Assert.Equal((seen.Round, 2 * seen.Round + 1), (seen.Results.Count, seen.Version));
                    Assert.Equal(CountResultDispositions.ConfirmedNoDifference, seen.Results[^1].Disposition);
                    break;
            }

            Assert.Equal(seen.Results.Count == 0 ? null : seen.Results[^1].Id, seen.LatestResultId);
            Assert.All(seen.Results.Take(seen.Results.Count - 1), r => Assert.Equal(CountResultDispositions.Superseded, r.Disposition));
        }

        await writer;
        Assert.Equal(CountStatuses.Completed, Ok(await Counts.GetAsync(admin, id, default)).Status);
    }

    private async Task<CountTaskResponse> ConfirmAsync(V2SessionPrincipal who, CountTaskResponse task) =>
        Ok(await Counts.ConfirmAsync(who, task.Id, new CountConfirmRequest(task.LatestResultId!, task.Round, task.Version), Guid.NewGuid().ToString(), default));

    private Task<V2Result<CountTaskResponse>> Start(V2SessionPrincipal who, string bin) =>
        Counts.StartAsync(who, new CountStartRequest(bin), Guid.NewGuid().ToString(), default);

    private Task<V2Result<CountTaskResponse>> Submit(V2SessionPrincipal who, CountTaskResponse task, string actual) =>
        Counts.SubmitAsync(who, task.Id, new CountSubmitRequest(actual, task.Round, task.Version), default);

    private Task<V2Result<InventoryTransactionResponse>> Run(V2SessionPrincipal who, string type, InventoryLine line, string? reason = null) =>
        Get<V2InventoryService>().ExecuteAsync(type, who, new InventoryOperationRequest([line], Reason: reason), Guid.NewGuid().ToString(), default);

    private async Task<(string Bin, string Product)> StockedBinAsync(V2SessionPrincipal admin, string quantity)
    {
        var product = await CreateProductAsync(admin, "Bolt " + Guid.NewGuid().ToString("N")[..6], 2);
        var bin = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest("A", "R1", "01", "01"), default)).Id;
        Ok(await Run(admin, TransactionTypesV2.Receipt, new InventoryLine(product.Id, ToBinId: bin, Quantity: quantity, Capacity: "100")));
        return (bin, product.Id);
    }

    private async Task<(string Bin, string Product)> SerialBinAsync(V2SessionPrincipal admin, string[] numbers)
    {
        var product = Ok(await Get<V2ProductService>().CreateAsync(admin, new ProductCreateRequest("Scanner", "1", 0, true, []), Guid.NewGuid().ToString(), default)).Id;
        var bin = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest("S", "R1", "01", "01"), default)).Id;
        Ok(await Run(admin, TransactionTypesV2.Receipt, new InventoryLine(product, ToBinId: bin, Capacity: "10", SerialNumbers: numbers)));
        return (bin, product);
    }
}
