using Wms.Application.V2.Admin;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B2b bins and racks through the real services on a temporary v2 file.</summary>
public sealed class V2BinServiceTests : V2ServiceTestBase
{
    private V2BinService Bins => Get<V2BinService>();

    [Fact]
    public async Task Positions_are_validated_and_unique_unicode_case_insensitively()
    {
        var supervisor = await SignInAsync("supervisor");
        var bin = Ok(await Bins.CreateAsync(supervisor, new BinWriteRequest("ä", "R1", "01", "01"), default));
        Assert.Equal("ä-R1-01-01", bin.Code);
        Assert.Equal("0", bin.Quantity);
        Assert.Equal(CapacityStatuses.NotConfigured, bin.CapacityStatus);
        Assert.Equal("20", bin.EffectiveLowThreshold);

        var taken = Fail(await Bins.CreateAsync(supervisor, new BinWriteRequest("Ä", "r1", "01", "01"), default));
        Assert.Equal(ProblemCodesV2.AlreadyExists, taken.Code);
        Assert.Equal(bin.Id, Assert.Single(taken.Subjects!).Id);

        foreach (var invalid in new[] { new BinWriteRequest("A-1", "R", "1", "1"), new BinWriteRequest("A 1", "R", "1", "1"), new BinWriteRequest(new string('A', 17), "R", "1", "1"), new BinWriteRequest("", "R", "1", "1") })
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Bins.CreateAsync(supervisor, invalid, default)).Code);
        }

        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'binCreate' AND Success = 0 AND FailureCode = 'VALIDATION_FAILED' AND ActorRole = 'warehouseSupervisor'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Bins"));
    }

    [Fact]
    public async Task Racks_use_current_defaults_label_levels_and_cells_and_reject_any_taken_position_as_a_whole()
    {
        var admin = await SignInAsync("admin");
        var rack = Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("A", "R1"), default));
        Assert.Equal((4, 6, 24, "A-R1-01-01", "A-R1-04-06"), (rack.Levels, rack.CellsPerLevel, rack.CreatedCount, rack.FirstCode, rack.LastCode));

        var tall = Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("B", "R1", 100, 1), default));
        Assert.Equal("B-R1-100-01", tall.LastCode);

        // One position of the next rack is already taken: nothing of the rack is created.
        Ok(await Bins.CreateAsync(admin, new BinWriteRequest("C", "R1", "02", "03"), default));
        var before = Scalar("SELECT COUNT(*) FROM Bins");
        var conflict = Fail(await Bins.CreateRackAsync(admin, new RackCreateRequest("c", "r1", 3, 3), default));
        Assert.Equal(ProblemCodesV2.AlreadyExists, conflict.Code);
        Assert.Equal("C-R1-02-03", Assert.Single(conflict.Subjects!).Label);
        Assert.Equal(before, Scalar("SELECT COUNT(*) FROM Bins"));
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Bins.CreateRackAsync(admin, new RackCreateRequest("A", "R1"), default)).Code);

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Bins.CreateRackAsync(admin, new RackCreateRequest("D", "R1", 0, 1), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Bins.CreateRackAsync(admin, new RackCreateRequest("D", "R1", 1, 101), default)).Code);

        // A new default affects only racks generated later.
        var settings = Ok(await Get<V2SettingsService>().GetAsync(default));
        Ok(await Get<V2SettingsService>().UpdateAsync(
            admin, new SettingsUpdateRequest(2, 2, settings.LowThreshold, settings.HighThreshold, settings.SessionHours, settings.Version), default));
        Assert.Equal(4, Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("E", "R1"), default)).CreatedCount);
        Assert.Equal(24L, Scalar("SELECT COUNT(*) FROM Bins WHERE Zone = 'A'"));
    }

    [Fact]
    public async Task Lists_filter_search_unicode_and_page_in_code_order()
    {
        var admin = await SignInAsync("admin");
        Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("Z", "R1", 2, 2), default));
        var accent = Ok(await Bins.CreateAsync(admin, new BinWriteRequest("é", "R1", "01", "01"), default));
        var product = await CreateProductAsync(admin, "écrou");
        Execute($"UPDATE Bins SET ProductId = {product.Id}, CapacityMicros = 10000000 WHERE Code = 'Z-R1-02-02'");

        var first = Ok(await Bins.ListAsync(null, "z", null, null, null, null, 3, default));
        Assert.Equal(new[] { "Z-R1-01-01", "Z-R1-01-02", "Z-R1-02-01" }, first.Items.Select(b => b.Code));
        var rest = Ok(await Bins.ListAsync(null, "z", null, null, null, first.NextCursor, 3, default));
        Assert.Equal("Z-R1-02-02", Assert.Single(rest.Items).Code);
        Assert.Null(rest.NextCursor);

        Assert.Equal(accent.Id, Assert.Single(Ok(await Bins.ListAsync("É-R1", null, null, null, null, null, null, default)).Items).Id);
        var byProduct = Assert.Single(Ok(await Bins.ListAsync("ÉCROU", null, null, null, null, null, null, default)).Items);
        Assert.Equal(("Z-R1-02-02", "écrou", "low"), (byProduct.Code, byProduct.ProductName, byProduct.CapacityStatus));
        Assert.Equal("0", byProduct.OccupancyPercent);
        Assert.Single(Ok(await Bins.ListAsync(product.Sku.ToLowerInvariant(), null, null, null, null, null, null, default)).Items);
        Assert.Single(Ok(await Bins.ListAsync(null, null, null, null, product.Id, null, null, default)).Items);
        Assert.Equal(2, Ok(await Bins.ListAsync(null, "Z", "R1", "02", null, null, null, default)).Items.Count);
    }

    [Fact]
    public async Task Moving_keeps_old_codes_reserved_and_needs_an_empty_bin_without_open_counts()
    {
        var admin = await SignInAsync("admin");
        var bin = Ok(await Bins.CreateAsync(admin, new BinWriteRequest("A", "R1", "01", "01"), default));
        var moved = Ok(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "09", bin.Version), default));
        Assert.Equal("A-R1-01-09", moved.Code);
        Assert.Equal(new[] { "A-R1-01-01" }, moved.HistoricalCodes);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Bins.CreateAsync(admin, new BinWriteRequest("a", "r1", "01", "01"), default)).Code);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "08", bin.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "08"), default)).Code);

        var product = await CreateProductAsync(admin, "Wire");
        Execute($"UPDATE Bins SET ProductId = {product.Id}, QuantityMicros = 1000000, CapacityMicros = 5000000 WHERE Id = {moved.Id}");
        Assert.Equal(ProblemCodesV2.StockNotZero, Fail(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "07", moved.Version), default)).Code);
        Assert.Equal(ProblemCodesV2.StockNotZero, Fail(await Bins.ArchiveAsync(admin, bin.Id, null, default)).Code);

        Execute($"UPDATE Bins SET QuantityMicros = 0 WHERE Id = {moved.Id}");
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound) " +
            $"VALUES ('CT-000001', {moved.Id}, 'A-R1-01-09', 1, 'Admin', 'systemAdministrator', 1, 'pendingConfirmation', 1)");
        Execute($"UPDATE Bins SET FrozenCountTaskId = (SELECT Id FROM CountTasks) WHERE Id = {moved.Id}");
        var task = Scalar("SELECT Id FROM CountTasks").ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Master-data changes use the contract code OPEN_COUNT_OR_INVESTIGATION (BIN_FROZEN is for inventory operations),
        // naming the bin and the count task.
        var frozen = Fail(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "07", moved.Version), default));
        AssertOpenCount(frozen, moved.Id, task);
        AssertOpenCount(Fail(await Bins.ArchiveAsync(admin, bin.Id, null, default)), moved.Id, task);

        Execute($"UPDATE Bins SET FrozenCountTaskId = NULL WHERE Id = {moved.Id}");
        Execute("UPDATE CountTasks SET Status = 'completed', CompletedAt = 2, HasUnresolvedInvestigation = 1");
        AssertOpenCount(Fail(await Bins.UpdateAsync(admin, bin.Id, new BinWriteRequest("A", "R1", "01", "07", moved.Version), default)), moved.Id, task);
        AssertOpenCount(Fail(await Bins.ArchiveAsync(admin, bin.Id, null, default)), moved.Id, task);
        Assert.Equal("A-R1-01-09", Text($"SELECT Code FROM Bins WHERE Id = {moved.Id}"));
        Assert.Equal(0L, Scalar("SELECT IsArchived FROM Bins"));
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE FailureCode = 'OPEN_COUNT_OR_INVESTIGATION'"));
    }

    [Fact]
    public async Task Archiving_hides_the_bin_resolves_its_capacity_alert_and_keeps_the_position_reserved()
    {
        var admin = await SignInAsync("admin");
        var bin = Ok(await Bins.CreateAsync(admin, new BinWriteRequest("A", "R1", "01", "01"), default));
        var product = await CreateProductAsync(admin, "Wire");
        Execute($"UPDATE Bins SET ProductId = {product.Id}, CapacityMicros = 5000000 WHERE Id = {bin.Id}");
        Execute($"INSERT INTO Alerts (Type, BinId, ProductId, Message, TriggerSnapshot, CreatedAt, Status) VALUES ('lowStock', {bin.Id}, {product.Id}, 'low', '{{}}', 1, 'open')");

        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Bins.ArchiveAsync(admin, bin.Id, new DeleteRequest(bin.Version + 5), default)).Code);
        Ok(await Bins.ArchiveAsync(admin, bin.Id, new DeleteRequest(bin.Version), default));

        Assert.Equal("resolved", Text("SELECT Status FROM Alerts"));
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Bins.GetAsync(bin.Id, default)).Code);
        Assert.Empty(Ok(await Bins.ListAsync(null, null, null, null, null, null, null, default)).Items);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Bins.ArchiveAsync(admin, bin.Id, null, default)).Code);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Bins.CreateAsync(admin, new BinWriteRequest("A", "R1", "01", "01"), default)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'binArchive' AND Success = 1"));
    }

    [Fact]
    public async Task A_rack_log_entry_names_every_created_bin()
    {
        var admin = await SignInAsync("admin");
        var rack = Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("M", "R1", 3, 3), default));
        Assert.Equal(9, rack.CreatedCount);

        Assert.Equal(9L, Scalar("SELECT json_array_length(Subjects) FROM ActivityLogs WHERE Type = 'rackCreate' AND Success = 1"));
        var middle = Scalar("SELECT Id FROM Bins WHERE Code = 'M-R1-02-02'");
        Assert.Equal(1L, Scalar(HistoryOfBin(middle))); // a middle bin finds the entry, not only the first and last
        Assert.Equal(
            Text("SELECT group_concat(Id, ',') FROM (SELECT Id FROM Bins ORDER BY Id)"),
            Text(
                "SELECT group_concat(json_extract(s.value, '$.id'), ',') FROM ActivityLogs l, json_each(l.Subjects) s " +
                "WHERE l.Type = 'rackCreate' AND l.Success = 1")); // aliases: json_each also has a 'type' column
    }

    [Fact]
    public async Task A_full_100_by_100_rack_names_all_10000_bins_without_truncation()
    {
        var admin = await SignInAsync("admin");
        var rack = Ok(await Bins.CreateRackAsync(admin, new RackCreateRequest("Q", "R1", 100, 100), default));

        Assert.Equal((10_000, "Q-R1-01-01", "Q-R1-100-100"), (rack.CreatedCount, rack.FirstCode, rack.LastCode));
        Assert.Equal(10_000L, Scalar("SELECT COUNT(*) FROM Bins"));
        Assert.Equal(10_000L, Scalar("SELECT json_array_length(Subjects) FROM ActivityLogs WHERE Type = 'rackCreate' AND Success = 1"));
        Assert.Equal(1L, Scalar(HistoryOfBin(Scalar("SELECT Id FROM Bins WHERE Code = 'Q-R1-50-50'"))));
        Assert.Equal(1L, Scalar(HistoryOfBin(Scalar("SELECT Id FROM Bins WHERE Code = 'Q-R1-100-100'"))));
    }

    [Fact]
    public async Task Bin_writes_need_catalog_write_of_the_current_role()
    {
        var receiver = await SignInAsync("receiver");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Bins.CreateAsync(receiver, new BinWriteRequest("A", "R1", "01", "01"), default)).Code);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Bins.CreateRackAsync(receiver, new RackCreateRequest("A", "R1"), default)).Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Bins"));
    }

    /// <summary>Log entries whose subjects contain the bin (how a bin's activity history is filtered).</summary>
    private static string HistoryOfBin(long binId) =>
        "SELECT COUNT(*) FROM ActivityLogs WHERE EXISTS (SELECT 1 FROM json_each(ActivityLogs.Subjects) s " +
        $"WHERE json_extract(s.value, '$.type') = 'bin' AND json_extract(s.value, '$.id') = '{binId}')";

    private static void AssertOpenCount(Wms.Application.V2.Common.V2Failure failure, string binId, string taskId)
    {
        Assert.Equal(ProblemCodesV2.OpenCountOrInvestigation, failure.Code);
        Assert.Contains(failure.Subjects!, s => s.Type == "bin" && s.Id == binId);
        Assert.Contains(failure.Subjects!, s => s.Type == "countTask" && s.Id == taskId);
    }
}
