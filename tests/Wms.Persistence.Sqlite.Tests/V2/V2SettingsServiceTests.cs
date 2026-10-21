using System.Text.Json.Nodes;
using Wms.Application.V2.Admin;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B2b settings: validation, versions, threshold conflicts, capacity alert re-evaluation and session hours.</summary>
public sealed class V2SettingsServiceTests : V2ServiceTestBase
{
    private V2SettingsService Settings => Get<V2SettingsService>();

    [Fact]
    public async Task Everyone_reads_only_the_current_administrator_role_writes_with_valid_values_and_version()
    {
        var current = Ok(await Settings.GetAsync(default));
        Assert.Equal((4, 6, "20", "80", 8), (current.DefaultRackLevels, current.DefaultCellsPerLevel, current.LowThreshold, current.HighThreshold, current.SessionHours));

        var supervisor = await SignInAsync("supervisor");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Settings.UpdateAsync(supervisor, With(current), default)).Code);

        var admin = await SignInAsync("admin");
        foreach (var invalid in new[]
                 {
                     With(current) with { DefaultRackLevels = 0 }, With(current) with { DefaultCellsPerLevel = 101 },
                     With(current) with { SessionHours = 169 }, With(current) with { LowThreshold = "20.123" },
                     With(current) with { LowThreshold = "80", HighThreshold = "80" }, With(current) with { HighThreshold = "100.01" },
                 })
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Settings.UpdateAsync(admin, invalid, default)).Code);
        }

        var saved = Ok(await Settings.UpdateAsync(admin, With(current) with { LowThreshold = "15.5", DefaultRackLevels = 3 }, default));
        Assert.Equal(("15.5", 3, current.Version + 1, "1", "系统管理员"), (saved.LowThreshold, saved.DefaultRackLevels, saved.Version, saved.UpdatedById, saved.UpdatedByName));
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Settings.UpdateAsync(admin, With(current), default)).Code);
        Assert.Equal(6L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'settingsUpdate' AND FailureCode = 'VALIDATION_FAILED'"));
    }

    [Fact]
    public async Task Defaults_that_conflict_with_any_bin_override_are_rejected_as_a_whole()
    {
        var admin = await SignInAsync("admin");
        var a = InsertBin("A", lowMicros: null, highMicros: 30_000_000); // own high 30 %
        var b = InsertBin("B", lowMicros: 90_000_000, highMicros: null); // own low 90 %
        InsertBin("C", lowMicros: 10_000_000, highMicros: 20_000_000); // both own: never conflicts
        var current = Ok(await Settings.GetAsync(default));

        var conflict = Fail(await Settings.UpdateAsync(admin, With(current) with { LowThreshold = "40", HighThreshold = "85" }, default));

        Assert.Equal(ProblemCodesV2.ThresholdConflict, conflict.Code);
        Assert.Equal(new[] { a.ToString(), b.ToString() }, conflict.Subjects!.Select(s => s.Id).Order());
        Assert.Equal(current.Version, Ok(await Settings.GetAsync(default)).Version); // nothing saved
        Assert.Equal(30_000_000L, Scalar($"SELECT HighThresholdMicros FROM Bins WHERE Id = {a}")); // no override dropped
        Assert.Equal(90_000_000L, Scalar($"SELECT LowThresholdMicros FROM Bins WHERE Id = {b}"));
    }

    [Fact]
    public async Task Threshold_changes_reevaluate_capacity_alerts_exclusively_without_rewriting_snapshots()
    {
        var admin = await SignInAsync("admin");
        var product = await CreateProductAsync(admin, "Wire", decimalPlaces: 2);
        var low = InsertBin("L", product.Id, quantity: 10, capacity: 100);       // 10 %
        var mid = InsertBin("M", product.Id, quantity: 50, capacity: 100);       // 50 %
        var over = InsertBin("O", product.Id, quantity: 120, capacity: 100);     // 120 %
        var empty = InsertBin("E", product.Id, quantity: 0, capacity: 100);      // kept product, 0 %
        var edge = InsertBin("X", product.Id, quantity: 15, capacity: 100);      // exactly on the next low threshold
        var free = InsertBin("F");                                               // no product: never alerted
        var investigation = InsertDiscrepancyAlert(low, product.Id);

        var settings = Ok(await Update(admin, "15", "80"));
        Assert.Equal("lowStock", OpenType(low));
        Assert.Equal("lowStock", OpenType(empty));
        Assert.Null(OpenType(edge)); // 15 % is not below 15 %: strict
        Assert.Null(OpenType(mid));
        Assert.Equal("overCapacity", OpenType(over));
        Assert.Null(OpenType(free));
        var snapshot = JsonNode.Parse(Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {low} AND Status = 'open' AND Type = 'lowStock'"))!;
        Assert.Equal(("10", "100", "10", "15", "80", "SKU-000001", 2), (
            snapshot["quantity"]!.GetValue<string>(), snapshot["capacity"]!.GetValue<string>(), snapshot["occupancyPercent"]!.GetValue<string>(),
            snapshot["lowThreshold"]!.GetValue<string>(), snapshot["highThreshold"]!.GetValue<string>(), snapshot["sku"]!.GetValue<string>(),
            snapshot["decimalPlaces"]!.GetValue<int>()));

        // Still low: no second record, original snapshot untouched.
        settings = Ok(await Update(admin, "12", "80", settings));
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {low} AND Type = 'lowStock'"));
        Assert.Contains("\"lowThreshold\":\"15\"", Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {low} AND Type = 'lowStock'"), StringComparison.Ordinal);

        // Type change: 50 % becomes high when high drops to 40 → its alert opens; 10 % stays low.
        settings = Ok(await Update(admin, "12", "40", settings));
        Assert.Equal("highStock", OpenType(mid));

        // Back to normal: low resolved, high resolved; over capacity continues once.
        Ok(await Update(admin, "5", "60", settings));
        Assert.Null(OpenType(low));
        Assert.Null(OpenType(mid));
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {over}"));
        Assert.Equal("open", Text($"SELECT Status FROM Alerts WHERE Id = {investigation}")); // investigations are untouched
        Assert.Equal(2L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {mid}") + Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {mid} AND Status = 'resolved'"));
        Assert.Equal(CapacityStatuses.OverCapacity, Ok(await Get<V2BinService>().GetAsync(over.ToString(), default)).CapacityStatus);
    }

    [Fact]
    public async Task Session_hours_apply_to_new_logins_only()
    {
        var admin = await SignInAsync("admin");
        var (_, before) = await LoginAsync("shipper");
        var current = Ok(await Settings.GetAsync(default));

        Ok(await Settings.UpdateAsync(admin, With(current) with { SessionHours = 2 }, default));
        var (_, after) = await LoginAsync("receiver");

        Assert.Equal(Clock.GetUtcNow().AddHours(8), before.ExpiresAt);
        Assert.Equal(Clock.GetUtcNow().AddHours(8).UtcTicks, Scalar($"SELECT ExpiresAt FROM LoginSessions WHERE Id = {before.SessionId}"));
        Assert.Equal(Clock.GetUtcNow().AddHours(2), after.ExpiresAt);
    }

    [Fact]
    public async Task The_settings_log_names_the_alerts_bins_and_products_that_changed_and_only_those()
    {
        var admin = await SignInAsync("admin");
        var product = await CreateProductAsync(admin, "Wire", decimalPlaces: 2);
        var low = InsertBin("L", product.Id, quantity: 10, capacity: 100);   // stays low
        var mid = InsertBin("M", product.Id, quantity: 50, capacity: 100);   // becomes high in the second change
        var over = InsertBin("O", product.Id, quantity: 120, capacity: 100); // stays over capacity
        var investigation = InsertDiscrepancyAlert(low, product.Id);
        var settings = Ok(await Update(admin, "15", "80"));
        var lowSnapshot = Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {low} AND Type = 'lowStock'");

        Ok(await Update(admin, "12", "40", settings));

        var subjects = Text(
            "SELECT group_concat(json_extract(value, '$.type') || ':' || json_extract(value, '$.id'), ',') FROM " +
            "(SELECT Subjects FROM ActivityLogs WHERE Type = 'settingsUpdate' AND Success = 1 ORDER BY Id DESC LIMIT 1), json_each(Subjects)").Split(',');
        var midAlert = Scalar($"SELECT Id FROM Alerts WHERE BinId = {mid} AND Type = 'highStock'");
        Assert.Contains($"bin:{mid}", subjects);
        Assert.Contains($"alert:{midAlert}", subjects);
        Assert.Contains($"product:{product.Id}", subjects);
        Assert.Contains("settings:1", subjects);
        Assert.DoesNotContain($"bin:{low}", subjects);  // continuing state: not touched, not named
        Assert.DoesNotContain($"bin:{over}", subjects);
        Assert.DoesNotContain($"alert:{investigation}", subjects);
        Assert.Equal(lowSnapshot, Text($"SELECT TriggerSnapshot FROM Alerts WHERE BinId = {low} AND Type = 'lowStock'"));
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {over}"));
        Assert.Equal("open", Text($"SELECT Status FROM Alerts WHERE Id = {investigation}"));

        // The first change's log names the bins whose alerts opened then.
        var first = Text(
            "SELECT group_concat(json_extract(value, '$.type') || ':' || json_extract(value, '$.id'), ',') FROM " +
            "(SELECT Subjects FROM ActivityLogs WHERE Type = 'settingsUpdate' AND Success = 1 ORDER BY Id LIMIT 1), json_each(Subjects)").Split(',');
        Assert.Contains($"bin:{low}", first);
        Assert.Contains($"bin:{over}", first);
        Assert.DoesNotContain($"bin:{mid}", first);
    }

    private static SettingsUpdateRequest With(SettingsResponse s) =>
        new(s.DefaultRackLevels, s.DefaultCellsPerLevel, s.LowThreshold, s.HighThreshold, s.SessionHours, s.Version);

    private async Task<Wms.Application.V2.Common.V2Result<SettingsResponse>> Update(
        Wms.Application.V2.Auth.V2SessionPrincipal admin, string low, string high, SettingsResponse? current = null)
    {
        current ??= Ok(await Settings.GetAsync(default));
        return await Settings.UpdateAsync(admin, With(current) with { LowThreshold = low, HighThreshold = high }, default);
    }

    private string? OpenType(long binId)
    {
        var count = Scalar($"SELECT COUNT(*) FROM Alerts WHERE BinId = {binId} AND Status = 'open' AND Type <> 'countDiscrepancy'");
        Assert.True(count <= 1, "at most one open capacity alert per bin");
        return count == 0 ? null : Text($"SELECT Type FROM Alerts WHERE BinId = {binId} AND Status = 'open' AND Type <> 'countDiscrepancy'");
    }

    private long InsertBin(string zone, string? productId = null, long quantity = 0, long? capacity = null, long? lowMicros = null, long? highMicros = null)
    {
        string Value(long? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        Execute(
            "INSERT INTO Bins (Zone, Rack, Level, Cell, Code, NormalizedCode, ProductId, QuantityMicros, CapacityMicros, LowThresholdMicros, HighThresholdMicros, CreatedAt, UpdatedAt) " +
            $"VALUES ('{zone}', 'R1', '01', '01', '{zone}-R1-01-01', '{zone}-R1-01-01', {productId ?? "NULL"}, {quantity * 1_000_000}, " +
            $"{Value(capacity * 1_000_000)}, {Value(lowMicros)}, {Value(highMicros)}, 0, 0)");
        return Scalar("SELECT max(Id) FROM Bins");
    }

    private long InsertDiscrepancyAlert(long binId, string productId)
    {
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CompletedAt, CurrentRound, HasUnresolvedInvestigation) " +
            $"VALUES ('CT-000001', {binId}, 'L-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'completed', 2, 1, 1)");
        Execute(
            "INSERT INTO CountResults (CountTaskId, Round, BinId, BinCode, ProductId, ProductName, Sku, Barcode, UnitName, DecimalPlaces, SerialTracking, " +
            "RoundStartedAt, BookQuantityMicros, ActualQuantityMicros, DifferenceMicros, SubmittedById, SubmittedByName, SubmittedByRole, SubmittedAt) " +
            $"VALUES ((SELECT Id FROM CountTasks), 1, {binId}, 'L-R1-01-01', {productId}, 'Wire', 'SKU-000001', 'BC-000001', '件', 2, 0, 1, 10000000, 9000000, -1000000, 1, 'Admin', 'systemAdministrator', 2)");
        Execute(
            "INSERT INTO Alerts (Type, BinId, ProductId, CountTaskId, CountResultId, Message, TriggerSnapshot, CreatedAt, Status) " +
            $"VALUES ('countDiscrepancy', {binId}, {productId}, (SELECT Id FROM CountTasks), (SELECT Id FROM CountResults), 'diff', '{{}}', 3, 'open')");
        return Scalar("SELECT max(Id) FROM Alerts");
    }
}
