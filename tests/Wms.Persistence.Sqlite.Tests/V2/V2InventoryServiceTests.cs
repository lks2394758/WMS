using Wms.Application.V2.Admin;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Application.V2.Inventory;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B3a inventory operations through the real services on a temporary v2 file (SQLite evidence only).</summary>
public sealed class V2InventoryServiceTests : V2ServiceTestBase
{
    private const string Receipt = TransactionTypesV2.Receipt;
    private const string Shipment = TransactionTypesV2.Shipment;
    private const string Movement = TransactionTypesV2.Movement;
    private const string Adjustment = TransactionTypesV2.Adjustment;

    private V2InventoryService Inventory => Get<V2InventoryService>();

    // ---- receipts, quantities, precision ----

    [Fact]
    public async Task A_receipt_fills_an_empty_bin_records_snapshots_and_opens_its_capacity_alert_in_the_same_transaction()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire", decimalPlaces: 2);
        var bin = await BinAsync(admin, "A");

        Assert.Equal(ProblemCodesV2.CapacityRequired, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "10"))).Code);
        var tx = Ok(await Run(Receipt, admin, new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "10.25", capacity: "100")], Source: " 供应商 A ")));

        Assert.Equal(("TX-000001", "receipt", "供应商 A"), (tx.Number, tx.Type, tx.Source));
        var line = Assert.Single(tx.Lines);
        Assert.Equal(("10.25", "0", "10.25", "Wire", 2), (line.Quantity, line.ToBefore, line.ToAfter, line.ProductName, line.DecimalPlaces));
        Assert.Null(line.FromBinId);
        Assert.Equal(10_250_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.Equal("lowStock", Text($"SELECT Type FROM Alerts WHERE BinId = {bin} AND Status = 'open'")); // 10.25 % < 20 %
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'receipt' AND Success = 1"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'receipt' AND Success = 0 AND FailureCode = 'CAPACITY_REQUIRED'"));
    }

    [Fact]
    public async Task Quantities_are_exact_within_product_precision_and_range_and_never_rounded()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire", decimalPlaces: 2);
        var bin = await BinAsync(admin, "A");

        Assert.Equal(ProblemCodesV2.PrecisionExceeded, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1.005", capacity: "100"))).Code);
        Assert.Equal(ProblemCodesV2.PrecisionExceeded, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1", capacity: "100.001"))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1e3", capacity: "100"))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1000000000000", capacity: "100"))).Code);

        // Packaging: 3 × 0.333333 = 0.999999 (6 decimals) does not fit 2; 1.5 × 0.333333 needs 7 decimals; both never rounded.
        Assert.Equal(ProblemCodesV2.PrecisionExceeded, Fail(await Run(Receipt, admin, Packed(wire.Id, bin, "3", "0.333333"))).Code);
        Assert.Equal(ProblemCodesV2.PrecisionExceeded, Fail(await Run(Receipt, admin, Packed(wire.Id, bin, "1.5", "0.333333"))).Code);
        Assert.Equal(ProblemCodesV2.QuantityOutOfRange, Fail(await Run(Receipt, admin, Packed(wire.Id, bin, "999999999999", "2"))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Receipt, admin, Packed(wire.Id, bin, "2", "12.5", quantity: "24"))).Code);

        var packed = Assert.Single(Ok(await Run(Receipt, admin, Packed(wire.Id, bin, "2", "12.5", quantity: "25"))).Lines);
        Assert.Equal(("25", "卷", "2", "12.5"), (packed.Quantity, packed.PackagingUnit, packed.PackagingQuantity, packed.ConversionFactor));

        // The largest single quantity, then one step more is out of range.
        var top = Ok(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "999999999974.99")));
        Assert.Equal("999999999999.99", top.Lines[0].ToAfter);
        Assert.Equal(ProblemCodesV2.QuantityOutOfRange, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "0.01"))).Code);
        Assert.Equal(999_999_999_999_990_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
    }

    [Fact]
    public async Task Lines_on_the_same_bin_use_accumulated_state_and_a_failing_line_rolls_back_the_whole_request()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire");
        var bin = await BinAsync(admin, "A");

        var tx = Ok(await Run(Receipt, admin, new InventoryOperationRequest(
            [Line(wire.Id, to: bin, quantity: "10", capacity: "100"), Line(wire.Id, to: bin, quantity: "5")])));
        Assert.Equal(("0", "10", "10", "15"), (tx.Lines[0].ToBefore, tx.Lines[0].ToAfter, tx.Lines[1].ToBefore, tx.Lines[1].ToAfter));

        var before = Snapshot();
        var failure = Fail(await Run(Shipment, admin, new InventoryOperationRequest(
            [Line(wire.Id, from: bin, quantity: "8"), Line(wire.Id, from: bin, quantity: "8")], Destination: "Shop")));
        Assert.Equal((ProblemCodesV2.InsufficientStock, "lines[1].quantity"), (failure.Code, failure.Field));
        Assert.Equal(before, Snapshot());
        Assert.Equal(15_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
    }

    [Fact]
    public async Task A_late_failure_rolls_back_inline_units_products_serial_items_alerts_and_counters()
    {
        var receiver = await SignInAsync("receiver");
        var admin = await SignInAsync("admin");
        var other = await CreateProductAsync(admin, "Other");
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        Ok(await Run(Receipt, admin, Line(other.Id, to: b, quantity: "1", capacity: "10")));
        var before = Snapshot();
        var units = Scalar("SELECT COUNT(*) FROM Units");

        var inline = new InventoryLine(
            ToBinId: a, Capacity: "10", GenerateSerialCount: 2,
            NewProduct: new InlineNewProduct("Scanner", 0, true, NewUnitName: "Gerät"));
        var mixed = Line(other.Id, to: b, quantity: "1", capacity: "5"); // b holds stock: capacity not allowed
        var failure = Fail(await Run(Receipt, receiver, new InventoryOperationRequest([inline, mixed])));

        Assert.Equal(ProblemCodesV2.CapacityNotAllowed, failure.Code);
        Assert.Equal(before, Snapshot());
        Assert.Equal(units, Scalar("SELECT COUNT(*) FROM Units"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Products WHERE Name = 'Scanner'"));

        // The receiving clerk may create the product and unit inside a receipt, but not through the catalog.
        var tx = Ok(await Run(Receipt, receiver, inline));
        Assert.Equal(2, Assert.Single(tx.Lines).Items!.Count);
        Assert.Equal(new[] { "SN-000001", "SN-000002" }, tx.Lines[0].Items!.Select(i => i.SerialNumber));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Units WHERE Name = 'Gerät'"));
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Get<V2UnitService>().CreateAsync(receiver, new UnitWriteRequest("Box"), default)).Code);

        // A second inline product with the same unit name (other case) reuses the unit.
        var again = new InventoryLine(
            ToBinId: await BinAsync(admin, "C"), Quantity: "1", Capacity: "3", NewProduct: new InlineNewProduct("Cable", 0, false, NewUnitName: "GERÄT"));
        Ok(await Run(Receipt, receiver, again));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Units WHERE NormalizedName = 'GERÄT'"));
    }

    // ---- bin configuration rules ----

    [Fact]
    public async Task Empty_bins_take_a_new_capacity_and_thresholds_and_stocked_bins_keep_theirs()
    {
        var admin = await SignInAsync("admin");
        var (wire, rope) = (await CreateProductAsync(admin, "Wire"), await CreateProductAsync(admin, "Rope"));
        var bin = await BinAsync(admin, "A");

        Ok(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "5", capacity: "10", low: "30")));
        Assert.Equal(ProblemCodesV2.CapacityNotAllowed, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1", capacity: "20"))).Code);
        Assert.Equal(ProblemCodesV2.BinProductMismatch, Fail(await Run(Receipt, admin, Line(rope.Id, to: bin, quantity: "1"))).Code);

        Ok(await Run(Shipment, admin, Line(wire.Id, from: bin, quantity: "5")));
        Assert.Equal((wire.Id, 10_000_000L), (Scalar($"SELECT ProductId FROM Bins WHERE Id = {bin}").ToString(System.Globalization.CultureInfo.InvariantCulture), Scalar($"SELECT CapacityMicros FROM Bins WHERE Id = {bin}")));
        Assert.Equal(ProblemCodesV2.CapacityRequired, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1"))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Receipt, admin, Line(rope.Id, to: bin, quantity: "1", capacity: "8", low: "50", high: "40"))).Code);

        Ok(await Run(Receipt, admin, Line(rope.Id, to: bin, quantity: "1", capacity: "8", high: "90"))); // another product replaces the retained one
        var state = Ok(await Get<V2BinService>().GetAsync(bin, default));
        Assert.Equal((rope.Id, "8", (string?)null, "90", "20"), (state.ProductId, state.Capacity, state.LowThreshold, state.HighThreshold, state.EffectiveLowThreshold));
    }

    [Fact]
    public async Task Frozen_bins_archived_bins_and_products_and_open_investigations_are_enforced()
    {
        var admin = await SignInAsync("admin");
        var (wire, rope) = (await CreateProductAsync(admin, "Wire"), await CreateProductAsync(admin, "Rope"));
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        Ok(await Run(Receipt, admin, Line(wire.Id, to: a, quantity: "5", capacity: "10")));

        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound) " +
            $"VALUES ('CT-000001', {a}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'pendingConfirmation', 1)");
        Execute($"UPDATE Bins SET FrozenCountTaskId = (SELECT Id FROM CountTasks) WHERE Id = {a}");
        var task = Scalar("SELECT Id FROM CountTasks").ToString(System.Globalization.CultureInfo.InvariantCulture);
        var frozen = Fail(await Run(Shipment, admin, Line(wire.Id, from: a, quantity: "1")));
        Assert.Equal(ProblemCodesV2.BinFrozen, frozen.Code);
        Assert.Contains(frozen.Subjects!, s => s.Type == "countTask" && s.Id == task); // the actual freezing task
        Assert.Contains(frozen.Subjects!, s => s.Type == "bin" && s.Id == a);
        Assert.Equal(ProblemCodesV2.BinFrozen, Fail(await Run(Receipt, admin, Line(wire.Id, to: a, quantity: "1"))).Code);
        Assert.Equal(ProblemCodesV2.BinFrozen, Fail(await Run(Movement, admin, Line(wire.Id, from: a, to: b, quantity: "1", capacity: "10"))).Code);
        Assert.Equal(ProblemCodesV2.BinFrozen, Fail(await Run(Adjustment, admin, new InventoryOperationRequest([Line(wire.Id, to: a, quantity: "-1")], Reason: "x"))).Code);

        // Count done, investigation open, bin emptied: only the same product may come back.
        Execute($"UPDATE Bins SET FrozenCountTaskId = NULL, QuantityMicros = 0 WHERE Id = {a}");
        Execute("UPDATE CountTasks SET Status = 'completed', CompletedAt = 2, HasUnresolvedInvestigation = 1");
        var investigation = Fail(await Run(Receipt, admin, Line(rope.Id, to: a, quantity: "1", capacity: "5")));
        Assert.Equal(ProblemCodesV2.InvestigationUnresolved, investigation.Code);
        Assert.Contains(investigation.Subjects!, s => s.Type == "countTask" && s.Id == task);
        Ok(await Run(Receipt, admin, Line(wire.Id, to: a, quantity: "1", capacity: "5")));

        Ok(await Get<V2BinService>().ArchiveAsync(admin, b, null, default));
        var archived = Fail(await Run(Receipt, admin, Line(wire.Id, to: b, quantity: "1", capacity: "5")));
        Assert.Equal(ProblemCodesV2.Archived, archived.Code);
        Assert.DoesNotContain(archived.Subjects!, s => s.Type == "countTask"); // no task behind it: none invented
        Ok(await Get<V2ProductService>().ArchiveAsync(admin, rope.Id, null, default));
        Assert.Equal(ProblemCodesV2.Archived, Fail(await Run(Receipt, admin, Line(rope.Id, to: await BinAsync(admin, "C"), quantity: "1", capacity: "5"))).Code);
    }

    [Fact]
    public async Task Each_operation_needs_its_permission_in_the_current_role_only()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire");
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        Ok(await Run(Receipt, admin, Line(wire.Id, to: a, quantity: "5", capacity: "10")));

        Execute("UPDATE Users SET Roles = '[\"receivingClerk\",\"shippingClerk\"]', Version = Version + 1 WHERE NormalizedUsername = 'RECEIVER'");
        var receiver = await SignInAsync("receiver"); // current role: receivingClerk
        Ok(await Run(Receipt, receiver, Line(wire.Id, to: a, quantity: "1")));
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Run(Shipment, receiver, Line(wire.Id, from: a, quantity: "1"))).Code);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Run(Movement, receiver, Line(wire.Id, from: a, to: b, quantity: "1", capacity: "5"))).Code);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Run(Adjustment, receiver, new InventoryOperationRequest([Line(wire.Id, to: a, quantity: "1")], Reason: "x"))).Code);

        var shipper = await SignInAsync("shipper");
        Ok(await Run(Shipment, shipper, Line(wire.Id, from: a, quantity: "2")));
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Run(Receipt, shipper, Line(wire.Id, to: a, quantity: "1"))).Code);
        Assert.Equal(4_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {a}"));
    }

    // ---- serial items ----

    [Fact]
    public async Task Serial_items_keep_their_identity_move_with_stock_and_reject_wrong_bins_duplicates_and_old_numbers()
    {
        var admin = await SignInAsync("admin");
        var scanner = await SerialProductAsync(admin, "Scanner");
        var printer = await SerialProductAsync(admin, "Printer");
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));

        var received = Ok(await Run(Receipt, admin, Line(scanner.Id, to: a, capacity: "10", serials: ["sc-1", "SC-2", "SC-3"])));
        var ids = received.Lines[0].Items!.ToDictionary(i => i.SerialNumber, i => i.SerializedItemId);
        Assert.Equal("3", received.Lines[0].Quantity);
        Assert.Equal(ProblemCodesV2.SerialCountMismatch, Fail(await Run(Receipt, admin, Line(scanner.Id, to: a, quantity: "2", serials: ["SC-4"]))).Code);
        Assert.Equal(ProblemCodesV2.SerialNumbersRequired, Fail(await Run(Receipt, admin, Line(scanner.Id, to: a, quantity: "1"))).Code);
        Assert.Equal(ProblemCodesV2.SerialAlreadyInStock, Fail(await Run(Receipt, admin, Line(scanner.Id, to: a, serials: ["SC-2"]))).Code);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Run(Receipt, admin, Line(printer.Id, to: b, capacity: "5", serials: ["Sc-1"]))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Shipment, admin, new InventoryOperationRequest(
            [Line(scanner.Id, from: a, serials: ["SC-2"]), Line(scanner.Id, from: a, serials: ["sc-2"])]))).Code); // one item, two lines

        var moved = Ok(await Run(Movement, admin, Line(scanner.Id, from: a, to: b, capacity: "5", serials: ["SC-2"])));
        Assert.Equal(ids["SC-2"], Assert.Single(moved.Lines[0].Items!).SerializedItemId);
        Assert.Equal(ProblemCodesV2.SerialNotInBin, Fail(await Run(Shipment, admin, Line(scanner.Id, from: a, serials: ["SC-2"]))).Code);

        Ok(await Run(Shipment, admin, Line(scanner.Id, from: a, serials: ["sc-1"])));
        Assert.Equal(0L, Scalar($"SELECT InStock FROM SerializedItems WHERE Id = {ids["sc-1"]}"));
        var back = Ok(await Run(Receipt, admin, Line(scanner.Id, to: a, serials: ["SC-1"]))); // same physical item returns
        Assert.Equal(ids["sc-1"], Assert.Single(back.Lines[0].Items!).SerializedItemId);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM SerializedItems"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM TransactionLineItems WHERE SerializedItemId = " + ids["sc-1"]));
        Assert.Equal("sc-1", Text("SELECT SerialNumber FROM TransactionLineItems ORDER BY Id LIMIT 1")); // number as it was then

        // A corrected number: the old one is reserved forever and points to the current number.
        Execute($"UPDATE SerializedItems SET SerialNumber = 'SC-30', NormalizedSerialNumber = 'SC-30', HistoricalSerialNumbers = '[\"SC-3\"]' WHERE Id = {ids["SC-3"]}");
        var old = Fail(await Run(Shipment, admin, Line(scanner.Id, from: a, serials: ["SC-3"])));
        Assert.Equal(ProblemCodesV2.SerialNumberSuperseded, old.Code);
        Assert.Equal("SC-30", Assert.Single(old.Subjects!).Label);
        Assert.Equal(ProblemCodesV2.SerialNumberSuperseded, Fail(await Run(Receipt, admin, Line(printer.Id, to: await BinAsync(admin, "P"), capacity: "5", serials: ["sc-3"]))).Code);

        // Negative adjustment names the items; the quantity is -count.
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Adjustment, admin, new InventoryOperationRequest([Line(scanner.Id, to: a, serials: ["SC-30"])], Reason: "lost"))).Code);
        var adjusted = Ok(await Run(Adjustment, admin, new InventoryOperationRequest([Line(scanner.Id, to: a, quantity: "-1", serials: ["SC-30"])], Reason: "lost")));
        Assert.Equal(("-1", "2", "1"), (adjusted.Lines[0].Quantity, adjusted.Lines[0].ToBefore, adjusted.Lines[0].ToAfter));

        // Tracking switched off later (at zero stock): a known item cannot come back as such.
        Execute($"UPDATE Bins SET QuantityMicros = 0 WHERE ProductId = {scanner.Id}");
        Execute($"UPDATE Products SET SerialTracking = 0 WHERE Id = {scanner.Id}");
        Assert.Equal(ProblemCodesV2.SerialTrackingDisabled, Fail(await Run(Receipt, admin, Line(scanner.Id, to: a, quantity: "1", serials: ["SC-2"]))).Code);
        Assert.Equal(ProblemCodesV2.SerialNumbersRequired, Fail(await Run(Receipt, admin, Line(scanner.Id, to: a, quantity: "1", serials: ["NEW-1"]))).Code);
    }

    [Fact]
    public async Task Generated_serial_numbers_skip_entered_and_historical_numbers()
    {
        var admin = await SignInAsync("admin");
        var scanner = await SerialProductAsync(admin, "Scanner");
        var bin = await BinAsync(admin, "A");
        Ok(await Run(Receipt, admin, Line(scanner.Id, to: bin, capacity: "10", serials: ["sn-000001"])));
        // The item was corrected twice: both old numbers stay reserved; SN-000003 is entered in the same request.
        Execute("UPDATE SerializedItems SET SerialNumber = 'X-1', NormalizedSerialNumber = 'X-1', HistoricalSerialNumbers = '[\"sn-000001\",\"SN-000002\"]'");

        var generated = Ok(await Run(Receipt, admin, new InventoryOperationRequest(
            [new InventoryLine(ProductId: scanner.Id, ToBinId: bin, GenerateSerialCount: 2), Line(scanner.Id, to: bin, serials: ["SN-000003"])])));

        Assert.Equal(new[] { "SN-000004", "SN-000005" }, generated.Lines[0].Items!.Select(i => i.SerialNumber));
        Assert.Equal(5L, Scalar("SELECT LastSerialNumber FROM SystemSettings"));
    }

    [Fact]
    public async Task Too_many_serial_items_in_one_request_are_rejected_before_any_change()
    {
        var admin = await SignInAsync("admin");
        var scanner = await SerialProductAsync(admin, "Scanner");
        var bin = await BinAsync(admin, "A");
        var before = Snapshot();

        var mixed = Fail(await Run(Receipt, admin, new InventoryOperationRequest(
            [Line(scanner.Id, to: bin, capacity: "10", serials: ["SN-A", "SN-B"]), new InventoryLine(ProductId: scanner.Id, ToBinId: bin, GenerateSerialCount: 9_999)])));
        var generated = Fail(await Run(Receipt, admin, new InventoryOperationRequest(
            [new InventoryLine(ProductId: scanner.Id, ToBinId: bin, Capacity: "10", GenerateSerialCount: 6_000),
                new InventoryLine(ProductId: scanner.Id, ToBinId: bin, GenerateSerialCount: 4_001)])));

        Assert.Equal((ProblemCodesV2.ValidationFailed, "lines[1]"), (mixed.Code, mixed.Field));
        Assert.Equal((ProblemCodesV2.ValidationFailed, "lines[1]"), (generated.Code, generated.Field));
        Assert.Equal(before, Snapshot());
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM SerializedItems"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'receipt' AND Success = 0 AND FailureCode = 'VALIDATION_FAILED'"));
    }

    // ---- idempotency ----

    [Fact]
    public async Task Retries_replay_the_original_immutable_transaction_even_after_catalog_changes_and_a_restart()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire");
        var bin = await BinAsync(admin, "A");
        var key = Guid.NewGuid().ToString();
        var request = new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "5", capacity: "10")]);

        var first = await Run(Receipt, admin, request, key);
        Ok(await Get<V2ProductService>().UpdateAsync(admin, wire.Id, new ProductUpdateRequest("Copper wire", "1", 0, false, [], wire.Version), default));
        var retry = await Run(Receipt, admin, request, key);

        Assert.True(retry.Replayed);
        Assert.Equal(first.Value.Id, retry.Value.Id);
        Assert.Equal("Wire", retry.Value.Lines[0].ProductName); // the snapshot, not the current name
        Assert.Equal(5_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
        Assert.Equal(ProblemCodesV2.IdempotencyConflict, Fail(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "6"), key)).Code);
        Assert.Equal(ProblemCodesV2.IdempotencyConflict, Fail(await Run(Shipment, admin, Line(wire.Id, from: bin, quantity: "5"), key)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Inventory.ExecuteAsync(Receipt, admin, request, null, default)).Code);

        await RestartAsync();
        var afterRestart = await Get<V2InventoryService>().ExecuteAsync(Receipt, admin, request, key, default);
        Assert.True(afterRestart.Replayed);
        Assert.Equal(first.Value.Number, afterRestart.Value.Number);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM InventoryTransactions"));
    }

    [Fact]
    public async Task Simultaneous_requests_with_one_key_commit_one_transaction()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire");
        var bin = await BinAsync(admin, "A");
        var key = Guid.NewGuid().ToString();
        var request = new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "5", capacity: "10")]);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Get<V2InventoryService>().ExecuteAsync(Receipt, admin, request, key, default))))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Single(results.Select(r => r.Value.Id).Distinct());
        Assert.Equal(3, results.Count(r => r.Replayed));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM InventoryTransactions"));
        Assert.Equal(5_000_000L, Scalar($"SELECT QuantityMicros FROM Bins WHERE Id = {bin}"));
    }

    // ---- adjustments linked to counts, alerts ----

    [Fact]
    public async Task Adjustments_may_link_a_count_result_of_the_same_bin_and_product_only()
    {
        var admin = await SignInAsync("admin");
        var (wire, rope) = (await CreateProductAsync(admin, "Wire"), await CreateProductAsync(admin, "Rope"));
        var bin = await BinAsync(admin, "A");
        Ok(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "5", capacity: "10")));
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CompletedAt, CurrentRound, HasUnresolvedInvestigation) " +
            $"VALUES ('CT-000001', {bin}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'completed', 2, 1, 1)");
        Execute(
            "INSERT INTO CountResults (CountTaskId, Round, BinId, BinCode, ProductId, ProductName, Sku, Barcode, UnitName, DecimalPlaces, SerialTracking, " +
            "RoundStartedAt, BookQuantityMicros, ActualQuantityMicros, DifferenceMicros, SubmittedById, SubmittedByName, SubmittedByRole, SubmittedAt) " +
            $"VALUES ((SELECT Id FROM CountTasks), 1, {bin}, 'A-R1-01-01', {wire.Id}, 'Wire', '{wire.Sku}', '{wire.Barcode}', '件', 0, 0, 1, 5000000, 4000000, -1000000, 1, 'Admin', 'systemAdministrator', 2)");
        var result = Scalar("SELECT Id FROM CountResults").ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Receipt, admin, new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "1")], CountResultId: result))).Code);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Run(Adjustment, admin, new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "-1")], Reason: "x", CountResultId: "999"))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Adjustment, admin, new InventoryOperationRequest([Line(rope.Id, to: bin, quantity: "-1")], Reason: "x", CountResultId: result))).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Run(Adjustment, admin, new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "-1")], Reason: "  "))).Code);

        var adjusted = Ok(await Run(Adjustment, admin, new InventoryOperationRequest([Line(wire.Id, to: bin, quantity: "-1")], Reason: "counted 4", CountResultId: result)));
        Assert.Equal((result, Scalar("SELECT Id FROM CountTasks").ToString(System.Globalization.CultureInfo.InvariantCulture)), (adjusted.CountResultId, adjusted.CountTaskId));
    }

    [Fact]
    public async Task Capacity_alerts_follow_the_changed_bins_exclusively_and_investigations_stay_open()
    {
        var admin = await SignInAsync("admin");
        var wire = await CreateProductAsync(admin, "Wire");
        var bin = await BinAsync(admin, "A");
        Ok(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "12", capacity: "10")));
        Assert.Equal("overCapacity", OpenCapacityAlert(bin));
        var overSnapshot = Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {bin} AND Type = 'overCapacity'");

        Ok(await Run(Receipt, admin, Line(wire.Id, to: bin, quantity: "1")));
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {bin}")); // continuing: no second record
        Assert.Equal(overSnapshot, Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {bin} AND Type = 'overCapacity'"));

        Ok(await Run(Shipment, admin, Line(wire.Id, from: bin, quantity: "8")));   // 5 of 10: normal
        Assert.Null(OpenCapacityAlert(bin));
        Ok(await Run(Shipment, admin, Line(wire.Id, from: bin, quantity: "4.000000")));   // 1 of 10: low
        Assert.Equal("lowStock", OpenCapacityAlert(bin));
        Assert.Contains("\"quantity\":\"1\"", Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {bin} AND Status = 'open'"), StringComparison.Ordinal);
    }

    // ---- history ----

    [Fact]
    public async Task The_ledger_lists_newest_first_with_filters_unicode_search_and_paging()
    {
        var admin = await SignInAsync("admin");
        var (ecrou, bolt) = (await CreateProductAsync(admin, "écrou"), await CreateProductAsync(admin, "Bolt"));
        var (a, b) = (await BinAsync(admin, "A"), await BinAsync(admin, "B"));
        Ok(await Run(Receipt, admin, Line(ecrou.Id, to: a, quantity: "5", capacity: "10")));
        Ok(await Run(Receipt, admin, Line(bolt.Id, to: b, quantity: "5", capacity: "10")));
        var shipper = await SignInAsync("shipper");
        Ok(await Run(Shipment, shipper, Line(ecrou.Id, from: a, quantity: "1")));
        Ok(await Run(Shipment, shipper, Line(ecrou.Id, from: a, quantity: "1")));
        var queries = Get<V2TransactionQueries>();

        var first = Ok(await queries.ListAsync("ÉCROU", null, null, null, null, null, null, null, 2, default));
        Assert.Equal(new[] { "TX-000004", "TX-000003" }, first.Items.Select(t => t.Number));
        var rest = Ok(await queries.ListAsync("ÉCROU", null, null, null, null, null, null, first.NextCursor, 2, default));
        Assert.Equal("TX-000001", Assert.Single(rest.Items).Number);
        Assert.Null(rest.NextCursor);

        Assert.Equal(2, Ok(await queries.ListAsync(null, Shipment, null, null, null, null, null, null, null, default)).Items.Count);
        Assert.Equal("TX-000002", Assert.Single(Ok(await queries.ListAsync(null, null, null, null, null, b, null, null, null, default)).Items).Number);
        Assert.Equal(2, Ok(await queries.ListAsync(null, null, null, null, null, null, shipper.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, default)).Items.Count);
        Assert.Equal(3, Ok(await queries.ListAsync(null, null, null, null, ecrou.Id, null, null, null, null, default)).Items.Count);
        var now = Clock.GetUtcNow();
        Assert.Equal(4, Ok(await queries.ListAsync(null, null, now.ToString("O"), now.AddSeconds(1).ToString("O"), null, null, null, null, null, default)).Items.Count);
        Assert.Empty(Ok(await queries.ListAsync(null, null, now.AddSeconds(1).ToString("O"), null, null, null, null, null, null, default)).Items);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await queries.ListAsync(null, "count", null, null, null, null, null, null, null, default)).Code);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await queries.GetAsync("999", default)).Code);
    }

    // ---- helpers ----

    private Task<V2Result<InventoryTransactionResponse>> Run(string type, V2SessionPrincipal who, InventoryOperationRequest request, string? key = null) =>
        Inventory.ExecuteAsync(type, who, request, key ?? Guid.NewGuid().ToString(), default);

    private Task<V2Result<InventoryTransactionResponse>> Run(string type, V2SessionPrincipal who, InventoryLine line, string? key = null) =>
        Run(type, who, new InventoryOperationRequest([line], Reason: type == Adjustment ? "test" : null), key);

    private static InventoryLine Line(
        string productId, string? from = null, string? to = null, string? quantity = null, string? capacity = null, string? low = null, string? high = null,
        IReadOnlyList<string>? serials = null) =>
        new(productId, from, to, quantity, capacity, low, high, SerialNumbers: serials);

    private static InventoryLine Packed(string productId, string bin, string packagingQuantity, string factor, string? quantity = null) =>
        new(productId, ToBinId: bin, Quantity: quantity, Capacity: "1000000", PackagingUnit: "卷", PackagingQuantity: packagingQuantity, ConversionFactor: factor);

    private async Task<string> BinAsync(V2SessionPrincipal admin, string zone) =>
        Ok(await Get<V2BinService>().CreateAsync(admin, new BinWriteRequest(zone, "R1", "01", "01"), default)).Id;

    private async Task<ProductResponse> SerialProductAsync(V2SessionPrincipal admin, string name) =>
        Ok(await Get<V2ProductService>().CreateAsync(admin, new ProductCreateRequest(name, "1", 0, true, []), Guid.NewGuid().ToString(), default));

    private string? OpenCapacityAlert(string bin)
    {
        var count = Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {bin} AND Status = 'open' AND Type <> 'countDiscrepancy'");
        Assert.True(count <= 1);
        return count == 0 ? null : Text($"SELECT Type FROM Alerts WHERE BinId = {bin} AND Status = 'open' AND Type <> 'countDiscrepancy'");
    }

    /// <summary>Everything an inventory request can change (to prove a failed request changed nothing).</summary>
    private string Snapshot() => Text(
        "SELECT (SELECT group_concat(Id || ':' || coalesce(ProductId, '') || ':' || QuantityMicros || ':' || coalesce(CapacityMicros, '') || ':' || Version, ',') FROM Bins) || '|' || " +
        "(SELECT COUNT(*) FROM Products) || '|' || (SELECT COUNT(*) FROM Units) || '|' || (SELECT COUNT(*) FROM SerializedItems) || '|' || " +
        "(SELECT COUNT(*) FROM InventoryTransactions) || '|' || (SELECT COUNT(*) FROM InventoryTransactionLines) || '|' || (SELECT COUNT(*) FROM Alerts) || '|' || " +
        "(SELECT LastSkuNumber || ',' || LastBarcodeNumber || ',' || LastSerialNumber || ',' || LastTransactionNumber FROM SystemSettings)");
}
