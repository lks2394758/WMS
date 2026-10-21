using Wms.Application.V2.Admin;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B3b serialized items through the real services on a temporary v2 file (SQLite evidence only).</summary>
public sealed class V2SerialServiceTests : V2ServiceTestBase
{
    private V2SerialService Serials => Get<V2SerialService>();

    [Fact]
    public async Task A_correction_keeps_the_physical_item_its_stock_and_every_earlier_snapshot()
    {
        var admin = await SignInAsync("admin");
        var scanner = await SerialProductAsync(admin, "Scanner");
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        var received = Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: a, Capacity: "10", SerialNumbers: ["SC-1", "SC-2"])));
        var id = received.Lines[0].Items![0].SerializedItemId;
        var before = Ok(await Serials.GetAsync(id, default));
        var bin = Text($"SELECT QuantityMicros || ':' || CapacityMicros || ':' || Version || ':' || ProductId FROM Bins WHERE Id = {a}");

        var corrected = Ok(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-10", " typo on the label ", before.Version), default));

        Assert.Equal((id, "SC-10", scanner.Id, true, a, before.Version + 1), (corrected.Id, corrected.SerialNumber, corrected.ProductId, corrected.InStock, corrected.BinId, corrected.Version));
        Assert.Equal(new[] { "SC-1" }, corrected.HistoricalSerialNumbers);
        var correction = Assert.Single(corrected.Corrections);
        Assert.Equal(("SC-1", "SC-10", "typo on the label", "系统管理员", "systemAdministrator", Clock.GetUtcNow()),
            (correction.PreviousSerialNumber, correction.NewSerialNumber, correction.Reason, correction.ActorName, correction.ActorRole, correction.OccurredAt));
        Assert.Equal("SC-1", Assert.Single(corrected.History).SerialNumber); // the receipt keeps the number as it was then
        Assert.Equal(bin, Text($"SELECT QuantityMicros || ':' || CapacityMicros || ':' || Version || ':' || ProductId FROM Bins WHERE Id = {a}"));
        Assert.Equal("SC-1", Text($"SELECT SerialNumber FROM TransactionLineItems WHERE SerializedItemId = {id}"));
        var log = Text("SELECT Subjects FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 1");
        Assert.Contains($"\"type\":\"serializedItem\",\"id\":\"{id}\"", log, StringComparison.Ordinal);
        Assert.Contains($"\"type\":\"product\",\"id\":\"{scanner.Id}\"", log, StringComparison.Ordinal);
        Assert.Contains($"\"type\":\"bin\",\"id\":\"{a}\"", log, StringComparison.Ordinal);

        // Inventory: the old number is refused with the current one; the new number moves, ships and returns the same item.
        var old = Fail(await Run(TransactionTypesV2.Shipment, admin, new InventoryLine(scanner.Id, FromBinId: a, SerialNumbers: ["sc-1"])));
        Assert.Equal(ProblemCodesV2.SerialNumberSuperseded, old.Code);
        Assert.Equal(("serializedItem", id, "SC-10"), (old.Subjects![0].Type, old.Subjects[0].Id, old.Subjects[0].Label));
        Assert.Equal(id, Ok(await Run(TransactionTypesV2.Movement, admin, new InventoryLine(scanner.Id, FromBinId: a, ToBinId: b, Capacity: "5", SerialNumbers: ["SC-10"]))).Lines[0].Items![0].SerializedItemId);
        Assert.Equal(id, Ok(await Run(TransactionTypesV2.Shipment, admin, new InventoryLine(scanner.Id, FromBinId: b, SerialNumbers: ["SC-10"]))).Lines[0].Items![0].SerializedItemId);
        Assert.Equal(id, Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: a, SerialNumbers: ["sc-10"]))).Lines[0].Items![0].SerializedItemId);
        Assert.Equal(ProblemCodesV2.SerialNumberSuperseded, Fail(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: a, SerialNumbers: ["SC-1"]))).Code);

        var after = Ok(await Serials.GetAsync(id, default));
        Assert.Equal(new[] { "SC-1", "SC-10", "SC-10", "SC-10" }, after.History.Select(h => h.SerialNumber));
        Assert.Equal(new[] { "receipt", "movement", "shipment", "receipt" }, after.History.Select(h => h.Type));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM SerializedItems"));
    }

    [Fact]
    public async Task Every_current_and_historical_number_of_every_item_stays_reserved()
    {
        var admin = await SignInAsync("admin");
        var (scanner, printer) = (await SerialProductAsync(admin, "Scanner"), await SerialProductAsync(admin, "Printer"));
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        var first = Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: a, Capacity: "10", SerialNumbers: ["äb-1", "X-1"])));
        Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(printer.Id, ToBinId: b, Capacity: "10", SerialNumbers: ["PR-1"])));
        var (ab, x) = (first.Lines[0].Items![0].SerializedItemId, first.Lines[0].Items![1].SerializedItemId);
        var abItem = Ok(await Serials.CorrectAsync(admin, ab, new SerialCorrectionRequest("ÄB-2", "fix", 1), default));

        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Serials.CorrectAsync(admin, x, new SerialCorrectionRequest("pr-1", "r", 1), default)).Code);    // another product's current
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Serials.CorrectAsync(admin, x, new SerialCorrectionRequest("ÄB-1", "r", 1), default)).Code);    // another item's old number
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Serials.CorrectAsync(admin, ab, new SerialCorrectionRequest("äB-1", "r", abItem.Version), default)).Code); // its own old number
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Serials.CorrectAsync(admin, ab, new SerialCorrectionRequest("äb-2", "r", abItem.Version), default)).Code); // its own current, other case
        var taken = Fail(await Serials.CorrectAsync(admin, x, new SerialCorrectionRequest("äb-2", "r", 1), default));
        Assert.Equal(("serialNumber", ab), (taken.Field, taken.Subjects![0].Id));
        Assert.Equal("X-1", Text($"SELECT SerialNumber FROM SerializedItems WHERE Id = {x}"));
    }

    [Fact]
    public async Task Invalid_numbers_reasons_and_versions_are_rejected_and_audited()
    {
        var admin = await SignInAsync("admin");
        var id = await OneItemAsync(admin);

        SerialCorrectionRequest[] invalid =
        [
            new(" SC-2", "r", 1), new("SC-2 ", "r", 1), new("SC\u00012", "r", 1), new("", "r", 1), new(new string('S', 129), "r", 1),
            new("SC-2", "   ", 1), new("SC-2", new string('r', 2001), 1), new("SC-2", "r", 0),
        ];
        foreach (var request in invalid)
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Serials.CorrectAsync(admin, id, request, default)).Code);
        }

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Serials.CorrectAsync(admin, "abc", new SerialCorrectionRequest("SC-2", "r", 1), default)).Code);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Serials.CorrectAsync(admin, "999", new SerialCorrectionRequest("SC-2", "r", 1), default)).Code);
        Assert.Equal(9L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 0 AND FailureCode = 'VALIDATION_FAILED' AND ActorRole = 'systemAdministrator'"));
        Assert.Equal("SC-1", Text($"SELECT SerialNumber FROM SerializedItems WHERE Id = {id}"));
        Assert.Equal(1L, Scalar($"SELECT Version FROM SerializedItems WHERE Id = {id}"));
    }

    [Fact]
    public async Task Stale_versions_conflict_and_only_a_current_version_same_number_request_is_a_no_op()
    {
        var admin = await SignInAsync("admin");
        var id = await OneItemAsync(admin);
        var corrected = Ok(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-2", "fix", 1), default));

        // The version rule comes first: a stale version conflicts even when the number is already the current one.
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-2", "fix", 1), default)).Code);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-3", "fix", 1), default)).Code);

        // With the current version, the current number again changes nothing and logs nothing.
        var again = Ok(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-2", "fix", corrected.Version), default));
        Assert.Equal(corrected.Version, again.Version);
        Assert.Equal(new[] { "SC-1" }, again.HistoricalSerialNumbers);
        Assert.Equal("SC-2", again.SerialNumber);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 1"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 0 AND FailureCode = 'VERSION_CONFLICT'"));

        // Two different corrections made from the same version: the shared writer lets exactly one through.
        var racing = await Task.WhenAll(
                Task.Run(() => Get<V2SerialService>().CorrectAsync(admin, id, new SerialCorrectionRequest("SC-4", "a", corrected.Version), default)),
                Task.Run(() => Get<V2SerialService>().CorrectAsync(admin, id, new SerialCorrectionRequest("SC-5", "b", corrected.Version), default)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, racing.Count(r => r.IsSuccess));
        Assert.Equal(ProblemCodesV2.VersionConflict, Assert.Single(racing, r => !r.IsSuccess).Failure!.Code);

        // Two identical corrections from one version at once: the first changes it; the second then holds a stale version
        // and conflicts. One change, one success log entry, one history entry.
        var current = Ok(await Serials.GetAsync(id, default));
        var same = await Task.WhenAll(
                Task.Run(() => Get<V2SerialService>().CorrectAsync(admin, id, new SerialCorrectionRequest("SC-9", "c", current.Version), default)),
                Task.Run(() => Get<V2SerialService>().CorrectAsync(admin, id, new SerialCorrectionRequest("SC-9", "c", current.Version), default)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, same.Count(r => r.IsSuccess));
        Assert.Equal(ProblemCodesV2.VersionConflict, Assert.Single(same, r => !r.IsSuccess).Failure!.Code);
        var final = Ok(await Serials.GetAsync(id, default));
        Assert.Equal(("SC-9", current.Version + 1), (final.SerialNumber, final.Version));
        Assert.Equal(current.HistoricalSerialNumbers.Count + 1, final.HistoricalSerialNumbers.Count);
        Assert.Equal(1, final.HistoricalSerialNumbers.Count(n => n == current.SerialNumber));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 1"));
        Assert.Equal(3, final.Corrections.Count);
    }

    [Fact]
    public async Task Only_the_current_role_with_serial_correction_may_correct()
    {
        var admin = await SignInAsync("admin");
        var id = await OneItemAsync(admin);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Serials.CorrectAsync(await SignInAsync("receiver"), id, new SerialCorrectionRequest("SC-2", "r", 1), default)).Code);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Serials.CorrectAsync(await SignInAsync("shipper"), id, new SerialCorrectionRequest("SC-2", "r", 1), default)).Code);

        // A clerk who also holds the supervisor role acts with the current (clerk) role only.
        Execute("UPDATE Users SET Roles = '[\"shippingClerk\",\"warehouseSupervisor\"]', Version = Version + 1 WHERE NormalizedUsername = 'SHIPPER'");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Serials.CorrectAsync(await SignInAsync("shipper"), id, new SerialCorrectionRequest("SC-2", "r", 1), default)).Code);

        Ok(await Serials.CorrectAsync(await SignInAsync("supervisor"), id, new SerialCorrectionRequest("SC-2", "r", 1), default));
        Assert.Equal("warehouseSupervisor", Text("SELECT ActorRole FROM ActivityLogs WHERE Type = 'serialCorrect' AND Success = 1"));
    }

    [Fact]
    public async Task Lists_search_current_and_old_numbers_unicode_case_insensitively_with_exact_filters_before_paging()
    {
        var admin = await SignInAsync("admin");
        var (scanner, printer) = (await SerialProductAsync(admin, "Scanner"), await SerialProductAsync(admin, "Printer"));
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        var items = Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: a, Capacity: "10", SerialNumbers: ["é-1", "x-2", "É-3", "é-4"]))).Lines[0].Items!;
        Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(printer.Id, ToBinId: b, Capacity: "10", SerialNumbers: ["é-9"])));
        Ok(await Serials.CorrectAsync(admin, items[1].SerializedItemId, new SerialCorrectionRequest("Y-2", "fix", 1), default)); // x-2 → Y-2
        Ok(await Run(TransactionTypesV2.Shipment, admin, new InventoryLine(scanner.Id, FromBinId: a, SerialNumbers: ["é-4"])));

        var first = Ok(await Serials.ListAsync("É-", scanner.Id, null, null, null, 2, default));
        Assert.Equal(new[] { "é-1", "É-3" }, first.Items.Select(i => i.SerialNumber));
        Assert.All(first.Items, i => Assert.True(WireId.IsValid(i.Id)));
        var rest = Ok(await Serials.ListAsync("É-", scanner.Id, null, null, first.NextCursor, 2, default));
        Assert.Equal("é-4", Assert.Single(rest.Items).SerialNumber);
        Assert.Null(rest.NextCursor);

        var byOld = Assert.Single(Ok(await Serials.ListAsync("X-2", null, null, null, null, null, default)).Items);
        Assert.Equal(("Y-2", "x-2"), (byOld.SerialNumber, byOld.MatchedHistoricalSerialNumber));
        Assert.Null(Assert.Single(Ok(await Serials.ListAsync("y-2", null, null, null, null, null, default)).Items).MatchedHistoricalSerialNumber);

        Assert.Equal(3, Ok(await Serials.ListAsync(null, scanner.Id, a, true, null, null, default)).Items.Count); // the in-stock picker
        Assert.Equal("é-4", Assert.Single(Ok(await Serials.ListAsync(null, null, null, false, null, null, default)).Items).SerialNumber);
        Assert.Equal("é-9", Assert.Single(Ok(await Serials.ListAsync(null, null, b, null, null, null, default)).Items).SerialNumber);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Serials.ListAsync(null, "abc", null, null, null, null, default)).Code);
    }

    [Fact]
    public async Task Archived_products_frozen_bins_and_renamed_products_keep_the_history_traceable()
    {
        var admin = await SignInAsync("admin");
        var scanner = await SerialProductAsync(admin, "Scanner");
        var bin = await BinAsync(admin, "A");
        var id = Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: bin, Capacity: "10", SerialNumbers: ["SC-1"]))).Lines[0].Items![0].SerializedItemId;

        // A frozen bin: correcting changes no stock, version or count evidence of the bin.
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound) " +
            $"VALUES ('CT-000001', {bin}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'pendingConfirmation', 1)");
        Execute($"UPDATE Bins SET FrozenCountTaskId = (SELECT Id FROM CountTasks) WHERE Id = {bin}");
        var binBefore = Text($"SELECT QuantityMicros || ':' || Version || ':' || FrozenCountTaskId FROM Bins WHERE Id = {bin}");
        Ok(await Serials.CorrectAsync(admin, id, new SerialCorrectionRequest("SC-2", "fix", 1), default));
        Assert.Equal(binBefore, Text($"SELECT QuantityMicros || ':' || Version || ':' || FrozenCountTaskId FROM Bins WHERE Id = {bin}"));
        Assert.Equal("pendingConfirmation", Text("SELECT Status FROM CountTasks"));
        Execute($"UPDATE Bins SET FrozenCountTaskId = NULL WHERE Id = {bin}");
        Execute("UPDATE CountTasks SET Status = 'cancelled', CancelledAt = 3, CancelReason = 'test'");

        // Ship out, rename, archive: the item and its history stay readable with the snapshots of the time.
        Ok(await Run(TransactionTypesV2.Shipment, admin, new InventoryLine(scanner.Id, FromBinId: bin, SerialNumbers: ["SC-2"])));
        var product = Ok(await Get<V2ProductService>().GetAsync(scanner.Id, default));
        Ok(await Get<V2ProductService>().UpdateAsync(admin, scanner.Id, new ProductUpdateRequest("Scanner Pro", "1", 0, true, [], product.Version), default));
        Ok(await Get<V2ProductService>().ArchiveAsync(admin, scanner.Id, null, default));

        var detail = Ok(await Serials.GetAsync(id, default));
        Assert.Equal(("Scanner Pro", false, (string?)null), (detail.ProductName, detail.InStock, detail.BinId));
        Assert.Equal(new[] { "SC-1", "SC-2" }, detail.History.Select(h => h.SerialNumber));
        Assert.Equal("A-R1-01-01", detail.History[0].ToBinCode);
        Assert.Equal("Scanner", Text($"SELECT ProductName FROM InventoryTransactionLines WHERE ProductId = {scanner.Id} ORDER BY Id LIMIT 1"));
        Assert.Single(Ok(await Serials.ListAsync("SC-1", null, null, null, null, null, default)).Items);
    }

    private async Task<string> OneItemAsync(V2SessionPrincipal admin)
    {
        var scanner = await SerialProductAsync(admin, "Scanner");
        var bin = await BinAsync(admin, "A");
        return Ok(await Run(TransactionTypesV2.Receipt, admin, new InventoryLine(scanner.Id, ToBinId: bin, Capacity: "10", SerialNumbers: ["SC-1"]))).Lines[0].Items![0].SerializedItemId;
    }

    private Task<V2Result<InventoryTransactionResponse>> Run(string type, V2SessionPrincipal who, InventoryLine line) =>
        Get<V2InventoryService>().ExecuteAsync(type, who, new InventoryOperationRequest([line]), Guid.NewGuid().ToString(), default);

    private async Task<string> BinAsync(V2SessionPrincipal admin, string zone) =>
        Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest(zone, "R1", "01", "01"), default)).Id;

    private async Task<ProductResponse> SerialProductAsync(V2SessionPrincipal admin, string name) =>
        Ok(await Get<V2ProductService>().CreateAsync(admin, new ProductCreateRequest(name, "1", 0, true, []), Guid.NewGuid().ToString(), default));
}
