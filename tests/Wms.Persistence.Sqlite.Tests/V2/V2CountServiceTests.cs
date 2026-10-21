using Wms.Application.V2.Admin;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Application.V2.Counts;
using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B4a count tasks through the real services on a temporary v2 file (SQLite evidence only).</summary>
public sealed class V2CountServiceTests : V2ServiceTestBase
{
    private V2CountService Counts => Get<V2CountService>();

    [Fact]
    public async Task Start_freezes_the_bin_hides_the_book_and_submit_stores_an_immutable_exact_result()
    {
        var admin = await SignInAsync("admin");
        var bin = await StockedBinAsync(admin, "2.5");

        var task = Ok(await Start(admin, bin));
        Assert.Equal(("CT-000001", CountStatuses.InProgress, 1, 1), (task.Number, task.Status, task.Round, task.Version));
        Assert.Equal(new[] { CountActions.Submit, CountActions.Cancel }, task.AllowedActions);
        Assert.Null(task.StatusBeforeRound);
        Assert.Equal(task.Id, Text($"SELECT FrozenCountTaskId FROM Bins WHERE Id = {bin}"));
        Assert.DoesNotContain("2.5", System.Text.Json.JsonSerializer.Serialize(Ok(await Counts.GetAsync(admin, task.Id, default))), StringComparison.Ordinal);
        Assert.Equal(ProblemCodesV2.BinFrozen, Fail(await Adjust(admin, bin, "1")).Code);

        Assert.Equal(ProblemCodesV2.PrecisionExceeded, Fail(await Submit(admin, task, "2.255")).Code);
        var submitted = Ok(await Submit(admin, task, "2.75", " found one "));
        var result = Assert.Single(submitted.Results);
        Assert.Equal(("2.5", "2.75", "0.25", "found one", CountResultDispositions.Pending), (result.BookQuantity, result.ActualQuantity, result.Difference, result.Note, result.Disposition));
        Assert.Equal((CountStatuses.PendingConfirmation, 2, result.Id), (submitted.Status, submitted.Version, submitted.LatestResultId));
        Assert.Equal(new[] { CountActions.Confirm, CountActions.Recount }, submitted.AllowedActions);
        Assert.Equal(2_500_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.Equal(task.Id, Text($"SELECT FrozenCountTaskId FROM Bins WHERE Id = {bin}"));
        Assert.ThrowsAny<Exception>(() => Execute($"UPDATE CountResults SET ActualQuantityMicros = 1 WHERE Id = {result.Id}"));

        // Retry: same values 200 unchanged; other values 409; a later version guard still applies to other commands.
        Assert.Equal(2, Ok(await Submit(admin, task, "2.75", "found one")).Version);
        Assert.Equal(ProblemCodesV2.CountRoundAlreadySubmitted, Fail(await Submit(admin, task, "3")).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM CountResults"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countSubmit' AND Success = 1"));
    }

    [Fact]
    public async Task Cancel_restores_each_earlier_state_and_rounds_never_go_back()
    {
        var admin = await SignInAsync("admin");
        var bin = await StockedBinAsync(admin, "4");

        // Round 1 cancel: cancelled, released, no result.
        var first = Ok(await Start(admin, bin));
        var cancelled = Ok(await Counts.CancelAsync(admin, first.Id, new CountCancelRequest("wrong bin", 1, 1), default));
        Assert.Equal((CountStatuses.Cancelled, "wrong bin", 1), (cancelled.Status, cancelled.CancelReason, cancelled.Round));
        Assert.Empty(cancelled.AllowedActions);
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND FrozenCountTaskId IS NOT NULL"));
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Counts.CancelAsync(admin, first.Id, new CountCancelRequest("again", 1, 1), default)).Code);

        // From pending: recount round 2, cancel → pending again, bin stays frozen, round stays 2.
        var task = Ok(await Start(admin, bin));
        task = Ok(await Submit(admin, task, "4"));
        var resultId = task.LatestResultId!;
        Assert.Equal(ProblemCodesV2.CountStateConflict, Fail(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("r", 2, task.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.CountStateConflict, Fail(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("r", 1, task.Version, "999"), default)).Code);
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("check", 1, task.Version, resultId), default));
        Assert.Equal((CountStatuses.InProgress, 2, CountStatuses.PendingConfirmation), (task.Status, task.Round, task.StatusBeforeRound));
        Assert.Equal(CountResultDispositions.Pending, Assert.Single(task.Results).Disposition);
        task = Ok(await Counts.CancelAsync(admin, task.Id, new CountCancelRequest("later", 2, task.Version), default));
        Assert.Equal((CountStatuses.PendingConfirmation, 2, resultId), (task.Status, task.Round, task.LatestResultId));
        Assert.Null(task.CancelledAt);
        Assert.Equal(task.Id, Text($"SELECT FrozenCountTaskId FROM Bins WHERE Id = {bin}"));

        // Round 3 after the cancelled round 2; submitting supersedes round 1.
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("again", 2, task.Version), default));
        Assert.Equal(3, task.Round);
        task = Ok(await Submit(admin, task, "3"));
        Assert.Equal(new[] { (1, CountResultDispositions.Superseded), (3, CountResultDispositions.Pending) }, task.Results.Select(r => (r.Round, r.Disposition)));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countCancel' AND Success = 1 AND Changes LIKE '%bookQuantity%' AND Description LIKE '%第 2 轮%'"));
    }

    [Fact]
    public async Task Cancel_from_completed_restores_completed_at_and_releases_the_bin()
    {
        var admin = await SignInAsync("admin");
        var bin = await StockedBinAsync(admin, "4");
        var task = Ok(await Submit(admin, Ok(await Start(admin, bin)), "3"));

        // B4b (confirm with an investigation) does not exist yet: set the completed state directly.
        var completedAt = Clock.GetUtcNow().UtcTicks;
        Execute($"UPDATE CountTasks SET Status = 'completed', CompletedAt = {completedAt}, HasUnresolvedInvestigation = 1, Version = Version + 1 WHERE Id = {task.Id}");
        Execute($"UPDATE Bins SET FrozenCountTaskId = NULL WHERE Id = {bin}");
        task = Ok(await Counts.GetAsync(admin, task.Id, default));
        Assert.Equal(new[] { CountActions.Recount, CountActions.Resolve }, task.AllowedActions);
        Assert.Equal(ProblemCodesV2.CountTaskExists, Fail(await Start(admin, bin)).Code);

        Clock.Advance(TimeSpan.FromHours(1));
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("recheck", 1, task.Version), default));
        Assert.Equal((CountStatuses.InProgress, 2, CountStatuses.Completed), (task.Status, task.Round, task.StatusBeforeRound));
        Assert.Null(task.CompletedAt);
        Assert.Equal(task.Id, Text($"SELECT FrozenCountTaskId FROM Bins WHERE Id = {bin}"));

        task = Ok(await Counts.CancelAsync(admin, task.Id, new CountCancelRequest("stop", 2, task.Version), default));
        Assert.Equal((CountStatuses.Completed, 2), (task.Status, task.Round));
        Assert.Equal(completedAt, task.CompletedAt!.Value.UtcTicks);
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND FrozenCountTaskId IS NOT NULL"));
    }

    [Fact]
    public async Task Start_rules_idempotency_roles_and_audit()
    {
        var admin = await SignInAsync("admin");
        var receiver = await SignInAsync("receiver");
        var bin = await StockedBinAsync(admin, "1");
        var empty = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest("E", "R1", "01", "01"), default)).Id;

        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Start(receiver, bin)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.StartAsync(admin, new CountStartRequest(bin), null, default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Counts.StartAsync(admin, new CountStartRequest(bin), "not-a-uuid", default)).Code);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Start(admin, "999")).Code);
        Assert.Equal(ProblemCodesV2.CountBinHasNoProduct, Fail(await Start(admin, empty)).Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM CountTasks"));

        var key = Guid.NewGuid().ToString();
        var task = Ok(await Counts.StartAsync(admin, new CountStartRequest(bin), key, default));
        var replay = await Counts.StartAsync(admin, new CountStartRequest(bin), key, default);
        Assert.True(replay.Replayed);
        Assert.Equal((task.Id, task.Number), (replay.Value!.Id, replay.Value.Number));
        Assert.Equal(ProblemCodesV2.IdempotencyConflict, Fail(await Counts.StartAsync(admin, new CountStartRequest(empty), key, default)).Code);
        var exists = Fail(await Start(admin, bin));
        Assert.Equal((ProblemCodesV2.CountTaskExists, task.Id), (exists.Code, exists.Subjects![0].Id));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM CountTasks"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countStart' AND Success = 1"));
        Assert.True(Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'countStart' AND Success = 0") >= 7);
        Assert.ThrowsAny<Exception>(() => Execute($"UPDATE CountTasks SET Number = 'CT-9' WHERE Id = {task.Id}"));

        // A receiver reads but gets no actions.
        Assert.Empty(Ok(await Counts.GetAsync(receiver, task.Id, default)).AllowedActions);
        var page = Ok(await Counts.ListAsync("ct-000", CountStatuses.InProgress, bin, null, null, null, default));
        Assert.Equal(task.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Concurrent_starts_and_submits_have_exactly_one_winner()
    {
        var admin = await SignInAsync("admin");
        var bin = await StockedBinAsync(admin, "2");

        var starts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Start(admin, bin))));
        var winner = Assert.Single(starts, s => s.IsSuccess).Value!;
        Assert.All(starts.Where(s => !s.IsSuccess), s => Assert.Equal(ProblemCodesV2.CountTaskExists, s.Failure!.Code));

        var submits = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Submit(admin, winner, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        Assert.Single(submits, s => s.IsSuccess);
        Assert.All(submits.Where(s => !s.IsSuccess), s => Assert.Equal(ProblemCodesV2.CountRoundAlreadySubmitted, s.Failure!.Code));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM CountResults"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM CountTasks"));
    }

    [Fact]
    public async Task Success_logs_of_submit_recount_and_cancel_name_product_and_results()
    {
        var admin = await SignInAsync("admin");
        var bin = await StockedBinAsync(admin, "4");
        var product = Text($"SELECT ProductId FROM Bins WHERE Id = {bin}");
        var task = Ok(await Submit(admin, Ok(await Start(admin, bin)), "4"));
        var result = task.LatestResultId!;
        task = Ok(await Counts.RecountAsync(admin, task.Id, new CountRecountRequest("check", 1, task.Version, result), default));
        Ok(await Counts.CancelAsync(admin, task.Id, new CountCancelRequest("stop", 2, task.Version), default));

        foreach (var type in new[] { "countSubmit", "countRecount", "countCancel" })
        {
            var log = Text($"SELECT Subjects FROM ActivityLogs WHERE Type = '{type}' AND Success = 1");
            Assert.Contains($"\"type\":\"countTask\",\"id\":\"{task.Id}\"", log, StringComparison.Ordinal);
            Assert.Contains($"\"type\":\"bin\",\"id\":\"{bin}\"", log, StringComparison.Ordinal);
            Assert.Contains($"\"type\":\"product\",\"id\":\"{product}\"", log, StringComparison.Ordinal);
            Assert.Contains($"\"type\":\"countResult\",\"id\":\"{result}\"", log, StringComparison.Ordinal);
        }
    }

    private Task<V2Result<CountTaskResponse>> Start(V2SessionPrincipal who, string bin) =>
        Counts.StartAsync(who, new CountStartRequest(bin), Guid.NewGuid().ToString(), default);

    private Task<V2Result<CountTaskResponse>> Submit(V2SessionPrincipal who, CountTaskResponse task, string actual, string? note = null) =>
        Counts.SubmitAsync(who, task.Id, new CountSubmitRequest(actual, task.Round, task.Version, note), default);

    private Task<V2Result<InventoryTransactionResponse>> Adjust(V2SessionPrincipal who, string bin, string quantity)
    {
        var product = Text($"SELECT ProductId FROM Bins WHERE Id = {bin}");
        return Get<V2InventoryService>().ExecuteAsync(
            TransactionTypesV2.Adjustment, who, new InventoryOperationRequest([new InventoryLine(product, ToBinId: bin, Quantity: quantity)], Reason: "test"),
            Guid.NewGuid().ToString(), default);
    }

    private async Task<string> StockedBinAsync(V2SessionPrincipal admin, string quantity)
    {
        var product = await CreateProductAsync(admin, "Bolt " + Guid.NewGuid().ToString("N")[..6], 2);
        var bin = Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest("A", "R1", "01", "01"), default)).Id;
        Ok(await Get<V2InventoryService>().ExecuteAsync(
            TransactionTypesV2.Receipt, admin, new InventoryOperationRequest([new InventoryLine(product.Id, ToBinId: bin, Quantity: quantity, Capacity: "100")]),
            Guid.NewGuid().ToString(), default));
        return bin;
    }
}
