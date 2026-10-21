using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.V2;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Sqlite.V2;
using static Wms.Persistence.Sqlite.Tests.V2.TestImages;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>
/// Units, categories and products through the real Application services on a temporary v2 file. Protected stock,
/// count and alert states that B3/B4 endpoints will create are written with SQL here, so the rules are tested now.
/// </summary>
public sealed class V2CatalogServiceTests : IAsyncLifetime
{
    private const long PresetUnitId = 1;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-catalog-tests", Guid.NewGuid().ToString("N"));
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private ServiceProvider _services = null!;
    private V2SessionPrincipal _admin = null!;
    private V2SessionPrincipal _supervisor = null!;
    private V2SessionPrincipal _receiver = null!;

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private V2UnitService Units => _services.GetRequiredService<V2UnitService>();

    private V2CategoryService Categories => _services.GetRequiredService<V2CategoryService>();

    private V2ProductService Products => _services.GetRequiredService<V2ProductService>();

    public async Task InitializeAsync()
    {
        _services = BuildServices();
        await V2SqlitePersistence.InitializeAsync(_services);
        await _services.GetRequiredService<V2Seeder>().SeedAsync(_password, CancellationToken.None);
        _admin = await SignInAsync("admin");
        _supervisor = await SignInAsync("supervisor");
        _receiver = await SignInAsync("receiver");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- units ----

    [Fact]
    public async Task Units_are_unique_case_insensitively_including_unicode_and_renames_keep_versions()
    {
        var created = Ok(await Units.CreateAsync(_admin, new UnitWriteRequest("  äpfel  "), default));
        Assert.Equal("äpfel", created.Name);

        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Units.CreateAsync(_supervisor, new UnitWriteRequest("ÄPFEL"), default)).Code);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Units.CreateAsync(_admin, new UnitWriteRequest("件"), default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Units.CreateAsync(_admin, new UnitWriteRequest(new string('x', 33)), default)).Code);

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Units.UpdateAsync(_admin, created.Id, new UnitWriteRequest("Kiste"), default)).Code); // no version
        var renamed = Ok(await Units.UpdateAsync(_admin, created.Id, new UnitWriteRequest("Kiste", created.Version), default));
        Assert.Equal(created.Version + 1, renamed.Version);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Units.UpdateAsync(_admin, created.Id, new UnitWriteRequest("Box", created.Version), default)).Code);
    }

    [Fact]
    public async Task The_preset_unit_and_units_used_by_archived_products_cannot_be_deleted()
    {
        var presetId = WireId.Format(PresetUnitId);
        var preset = Fail(await Units.DeleteAsync(_admin, presetId, null, default));
        Assert.Equal(ProblemCodesV2.InUse, preset.Code);

        var unit = Ok(await Units.CreateAsync(_admin, new UnitWriteRequest("Rolle"), default));
        var product = await CreateProductAsync("Kabel", unit.Id);
        Ok(await Products.ArchiveAsync(_admin, product.Id, null, default));
        Assert.Equal(ProblemCodesV2.InUse, Fail(await Units.DeleteAsync(_admin, unit.Id, null, default)).Code);

        var unused = Ok(await Units.CreateAsync(_admin, new UnitWriteRequest("Sack"), default));
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Units.DeleteAsync(_admin, unused.Id, new DeleteRequest(99), default)).Code);
        Ok(await Units.DeleteAsync(_admin, unused.Id, new DeleteRequest(unused.Version), default));
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Units.GetAsync(unused.Id, default)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitDelete' AND Success = 1"));
    }

    // ---- categories ----

    [Fact]
    public async Task Category_tree_rejects_cycles_and_sibling_duplicates_and_filters_with_descendants()
    {
        var tools = Ok(await Categories.CreateAsync(_admin, new CategoryWriteRequest("Tools"), default));
        var power = Ok(await Categories.CreateAsync(_admin, new CategoryWriteRequest("Power", tools.Id), default));
        var drills = Ok(await Categories.CreateAsync(_admin, new CategoryWriteRequest("Drills", power.Id), default));
        Assert.Equal("Tools / Power / Drills", drills.Path);

        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Categories.CreateAsync(_admin, new CategoryWriteRequest("TOOLS"), default)).Code);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Categories.CreateAsync(_admin, new CategoryWriteRequest("power", tools.Id), default)).Code);
        Ok(await Categories.CreateAsync(_admin, new CategoryWriteRequest("Power"), default)); // another parent (top level): allowed

        var cycle = Fail(await Categories.UpdateAsync(_admin, tools.Id, new CategoryWriteRequest("Tools", drills.Id, tools.Version), default));
        Assert.Equal(ProblemCodesV2.ValidationFailed, cycle.Code);
        Assert.Equal("parentId", cycle.Field);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Categories.UpdateAsync(_admin, tools.Id, new CategoryWriteRequest("Tools", tools.Id, tools.Version), default)).Code);

        var drill = await CreateProductAsync("Drill", categoryIds: [drills.Id]);
        await CreateProductAsync("Hammer");
        var underTools = Ok(await Products.ListAsync(null, tools.Id, null, null, default));
        Assert.Equal(drill.Id, Assert.Single(underTools.Items).Id);

        Assert.Equal(ProblemCodesV2.InUse, Fail(await Categories.DeleteAsync(_admin, power.Id, null, default)).Code); // has a child
        Assert.Equal(ProblemCodesV2.InUse, Fail(await Categories.DeleteAsync(_admin, drills.Id, null, default)).Code); // has a product
        var page = Ok(await Categories.ListAsync(null, null, null, 2, default));
        Assert.Equal(2, page.Items.Count);
        var rest = Ok(await Categories.ListAsync(null, null, page.NextCursor, 10, default));
        Assert.Equal(2, rest.Items.Count);
        Assert.Null(rest.NextCursor);
    }

    // ---- product codes and create retries ----

    [Fact]
    public async Task Generated_codes_skip_every_used_and_historical_code_and_survive_a_restart()
    {
        var first = await CreateProductAsync("A");
        Assert.Equal(("SKU-000001", "BC-000001"), (first.Sku, first.Barcode));
        await CreateProductAsync("Manual", sku: "sku-000002", barcode: "bc-000002"); // takes the next numbers by hand
        var third = await CreateProductAsync("B");
        Assert.Equal(("SKU-000003", "BC-000003"), (third.Sku, third.Barcode));

        // Rename the first product's SKU: the old value stays reserved forever (case-insensitive).
        var moved = Ok(await Products.UpdateAsync(_admin, first.Id, Update(first, sku: "WIRE-1"), default));
        Assert.Equal(new[] { "SKU-000001" }, moved.HistoricalSkus);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await CreateProductResultAsync("C", sku: "sku-000001")).Code);
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Products.UpdateAsync(_admin, third.Id, Update(third, sku: "Sku-000001"), default)).Code);

        // Archived products keep their codes, Unicode case-insensitively.
        var umlaut = await CreateProductAsync("D", sku: "ÄRMEL-1");
        Ok(await Products.ArchiveAsync(_admin, umlaut.Id, null, default));
        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await CreateProductResultAsync("E", sku: "ärmel-1")).Code);

        // The counter is persisted: a new provider on the same file continues after the last number.
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _services = BuildServices();
        var next = await CreateProductAsync("F");
        Assert.Equal("SKU-000004", next.Sku);
        Assert.True(Scalar("SELECT LastSkuNumber FROM SystemSettings") >= 4);
    }

    [Fact]
    public async Task Create_is_idempotent_per_actor_key_and_body()
    {
        var key = Guid.NewGuid().ToString();
        var request = NewProduct("Scanner");

        var first = await Products.CreateAsync(_admin, request, key, default);
        var retry = await Products.CreateAsync(_admin, request, key, default);
        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Value.Id, retry.Value.Id);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Products"));
        Assert.Equal(1L, Scalar("SELECT LastSkuNumber FROM SystemSettings")); // the replay consumed no number

        Assert.Equal(ProblemCodesV2.IdempotencyConflict, Fail(await Products.CreateAsync(_admin, request with { Name = "Other" }, key, default)).Code);
        var otherActor = Ok(await Products.CreateAsync(_supervisor, request, key, default)); // keys are per account
        Assert.NotEqual(first.Value.Id, otherActor.Id);

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Products.CreateAsync(_admin, request, null, default)).Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Products.CreateAsync(_admin, request, "not-a-uuid", default)).Code);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM Products"));
    }

    [Fact]
    public async Task A_failure_after_generating_a_code_rolls_everything_back_and_is_logged_separately()
    {
        var existing = await CreateProductAsync("A", barcode: "TAKEN-1");
        var before = Scalar("SELECT LastSkuNumber FROM SystemSettings");

        // The SKU is generated (counter incremented inside the transaction), then the entered barcode is taken.
        var failure = Fail(await CreateProductResultAsync("B", barcode: "taken-1"));

        Assert.Equal(ProblemCodesV2.AlreadyExists, failure.Code);
        Assert.Equal("barcode", failure.Field);
        Assert.Equal(existing.Id, Assert.Single(failure.Subjects!).Id);
        Assert.Equal(before, Scalar("SELECT LastSkuNumber FROM SystemSettings"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Products"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'productCreate' AND Success = 0 AND FailureCode = 'ALREADY_EXISTS'"));
    }

    // ---- key fields, stock, counts, archive ----

    [Fact]
    public async Task Key_fields_and_archive_need_zero_stock_and_no_open_count_or_investigation()
    {
        var product = await CreateProductAsync("Wire");
        var bin = InsertBin("A", product.Id, quantityMicros: 1_500_000, capacityMicros: 10_000_000);

        Assert.Equal(ProblemCodesV2.StockNotZero, Fail(await Products.UpdateAsync(_admin, product.Id, Update(product, decimalPlaces: 2), default)).Code);
        Assert.Equal(ProblemCodesV2.StockNotZero, Fail(await Products.UpdateAsync(_admin, product.Id, Update(product, barcode: "NEW-1"), default)).Code);
        Assert.Equal(ProblemCodesV2.StockNotZero, Fail(await Products.ArchiveAsync(_admin, product.Id, null, default)).Code);
        var renamed = Ok(await Products.UpdateAsync(_admin, product.Id, Update(product, name: "Copper wire"), default)); // non-key: allowed
        Assert.Equal("1.5", renamed.TotalQuantity);

        Execute($"UPDATE Bins SET QuantityMicros = 0 WHERE Id = {bin}");
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CompletedAt, CurrentRound, HasUnresolvedInvestigation) " +
            $"VALUES ('CT-000001', {bin}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'completed', 2, 1, 1)"); // completed, investigation open
        var open = Fail(await Products.UpdateAsync(_admin, product.Id, Update(renamed, unitId: PresetUnitId, decimalPlaces: 3), default));
        Assert.Equal(ProblemCodesV2.OpenCountOrInvestigation, open.Code);
        Assert.Equal(ProblemCodesV2.OpenCountOrInvestigation, Fail(await Products.ArchiveAsync(_admin, product.Id, null, default)).Code);
        Assert.Equal(0L, Scalar("SELECT DecimalPlaces FROM Products"));
    }

    [Fact]
    public async Task A_unit_or_precision_change_clears_zero_stock_bin_capacity_and_resolves_capacity_alerts()
    {
        var product = await CreateProductAsync("Wire");
        var bin = InsertBin("A", product.Id, quantityMicros: 0, capacityMicros: 100_000_000, lowMicros: 10_000_000);
        Execute(
            "INSERT INTO Alerts (Type, BinId, ProductId, Message, TriggerSnapshot, CreatedAt, Status) " +
            $"VALUES ('lowStock', {bin}, {product.Id}, 'low', '{{}}', 1, 'open')");

        var updated = Ok(await Products.UpdateAsync(_admin, product.Id, Update(product, decimalPlaces: 2), default));

        Assert.Equal(2, updated.DecimalPlaces);
        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND ProductId = {product.Id} AND CapacityMicros IS NULL AND LowThresholdMicros IS NULL"));
        Assert.Equal("resolved", Text("SELECT Status FROM Alerts"));
        Assert.Equal("low", Text("SELECT Message FROM Alerts")); // evidence kept
        Assert.Contains("binCapacity", Text("SELECT Changes FROM ActivityLogs WHERE Type = 'productUpdate'"), StringComparison.Ordinal);
        Assert.Equal("notConfigured", Assert.Single(updated.Bins).CapacityStatus);
    }

    [Fact]
    public async Task Archive_releases_zero_stock_bins_resolves_alerts_hides_the_product_and_keeps_history()
    {
        var product = await CreateProductAsync("Wire");
        var bin = InsertBin("A", product.Id, quantityMicros: 0, capacityMicros: 100_000_000);
        Execute(
            "INSERT INTO Alerts (Type, BinId, ProductId, Message, TriggerSnapshot, CreatedAt, Status) " +
            $"VALUES ('lowStock', {bin}, {product.Id}, 'low', '{{}}', 1, 'open')");

        Ok(await Products.ArchiveAsync(_admin, product.Id, new DeleteRequest(product.Version), default));

        Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM Bins WHERE Id = {bin} AND ProductId IS NULL AND CapacityMicros IS NULL"));
        Assert.Equal("resolved", Text("SELECT Status FROM Alerts"));
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Products.GetAsync(product.Id, default)).Code);
        Assert.Empty(Ok(await Products.ListAsync(null, null, null, null, default)).Items);
        Assert.Equal(ProblemCodesV2.NotFound, Fail(await Products.ArchiveAsync(_admin, product.Id, null, default)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Products WHERE IsArchived = 1"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'productArchive' AND Success = 1"));
    }

    [Fact]
    public async Task Versions_and_serial_tracking_precision_are_enforced()
    {
        var product = await CreateProductAsync("Scanner");
        Ok(await Products.UpdateAsync(_admin, product.Id, Update(product, name: "Scanner 2"), default));

        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Products.UpdateAsync(_admin, product.Id, Update(product, name: "Stale"), default)).Code);
        Assert.Equal(
            ProblemCodesV2.ValidationFailed,
            Fail(await CreateProductResultAsync("Serial", decimalPlaces: 2, serialTracking: true)).Code);
    }

    // ---- images ----

    [Fact]
    public async Task Images_are_validated_kept_replaced_removed_and_never_listed()
    {
        var created = Ok(await Products.CreateAsync(
            _admin, NewProduct("Photo") with { ImageBase64 = Base64(Png()), ImageContentType = "image/png" }, Guid.NewGuid().ToString(), default));
        Assert.Equal(Base64(Png()), created.ImageBase64);

        var listed = Assert.Single(Ok(await Products.ListAsync(null, null, null, null, default)).Items);
        Assert.True(listed.HasImage); // the list row type has no image members at all

        var kept = Ok(await Products.UpdateAsync(_admin, created.Id, Update(created, name: "Photo 2"), default));
        Assert.Equal("image/png", kept.ImageContentType);

        var both = Update(kept) with { RemoveImage = true, ImageBase64 = Base64(Jpeg()), ImageContentType = "image/jpeg" };
        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Products.UpdateAsync(_admin, created.Id, both, default)).Code);

        var fake = Update(kept) with { ImageBase64 = Base64(Png()[..^5]), ImageContentType = "image/png" };
        Assert.Equal(ProblemCodesV2.InvalidImage, Fail(await Products.UpdateAsync(_admin, created.Id, fake, default)).Code);

        var replaced = Ok(await Products.UpdateAsync(_admin, created.Id, Update(kept) with { ImageBase64 = Base64(Jpeg()), ImageContentType = "image/jpeg" }, default));
        Assert.Equal("image/jpeg", replaced.ImageContentType);

        var removed = Ok(await Products.UpdateAsync(_admin, created.Id, Update(replaced) with { RemoveImage = true }, default));
        Assert.False(removed.HasImage);
        Assert.Null(removed.ImageBase64);

        var logs = Text("SELECT group_concat(coalesce(Changes, ''), '|') FROM ActivityLogs");
        Assert.Contains("image/jpeg", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64(Png()), logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Base64(Jpeg()), logs, StringComparison.Ordinal);
    }

    // ---- permissions and sessions ----

    [Fact]
    public async Task Writes_need_the_current_role_permission_and_a_still_valid_session()
    {
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Units.CreateAsync(_receiver, new UnitWriteRequest("Box"), default)).Code);
        Assert.NotEmpty(Ok(await Units.ListAsync(null, null, null, default)).Items); // reading is allowed

        // A multi-role account acts with its current role only, not the union of its roles.
        Execute("UPDATE Users SET Roles = '[\"receivingClerk\",\"warehouseSupervisor\"]', Version = Version + 1 WHERE NormalizedUsername = 'RECEIVER'");
        var multi = await SignInAsync("receiver");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Units.CreateAsync(multi, new UnitWriteRequest("Box"), default)).Code);

        // The admin's session was replaced by a newer login while this request "waited": the old principal cannot write.
        var stale = _admin;
        await SignInAsync("admin");
        Assert.Equal(ProblemCodesV2.SessionRevoked, Fail(await Units.CreateAsync(stale, new UnitWriteRequest("Box"), default)).Code);

        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Units WHERE NormalizedName = 'BOX'"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitCreate' AND Success = 0"));
    }

    // ---- exact numbers ----

    [Fact]
    public async Task Totals_and_ids_beyond_javascript_safe_integers_stay_exact()
    {
        Execute("UPDATE sqlite_sequence SET seq = 9007199254740993 WHERE name = 'Products'");
        Execute("INSERT OR IGNORE INTO sqlite_sequence (name, seq) VALUES ('Products', 9007199254740993)");
        var product = await CreateProductAsync("Bulk", decimalPlaces: 6);
        Assert.Equal("9007199254740994", product.Id);

        const long max = 999_999_999_999_999_999;
        InsertBin("A", product.Id, quantityMicros: max, capacityMicros: max);
        InsertBin("B", product.Id, quantityMicros: max - 1, capacityMicros: max);

        var detail = Ok(await Products.GetAsync(product.Id, default));
        Assert.Equal("1999999999999.999997", detail.TotalQuantity);
        Assert.Equal(WireQuantity.MaxText, detail.Bins[0].Quantity);
        Assert.Equal("100", detail.Bins[0].OccupancyPercent);
        Assert.Equal("1999999999999.999997", Assert.Single(Ok(await Products.ListAsync("bulk", null, null, null, default)).Items).TotalQuantity);
    }

    // ---- helpers ----

    private static T Ok<T>(V2Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException($"Expected success, got {result.Failure!.Code}: {result.Failure.Message}");

    private static V2Failure Fail<T>(V2Result<T> result) =>
        result.Failure ?? throw new Xunit.Sdk.XunitException("Expected a failure, got success.");

    private ProductCreateRequest NewProduct(
        string name, string? unitId = null, string? sku = null, string? barcode = null, int decimalPlaces = 0, bool serialTracking = false,
        IReadOnlyList<string>? categoryIds = null) =>
        new(name, unitId ?? WireId.Format(PresetUnitId), decimalPlaces, serialTracking, categoryIds ?? [], sku, barcode);

    private Task<V2Result<ProductResponse>> CreateProductResultAsync(
        string name, string? sku = null, string? barcode = null, int decimalPlaces = 0, bool serialTracking = false) =>
        Products.CreateAsync(_admin, NewProduct(name, null, sku, barcode, decimalPlaces, serialTracking), Guid.NewGuid().ToString(), default);

    private async Task<ProductResponse> CreateProductAsync(
        string name, string? unitId = null, string? sku = null, string? barcode = null, int decimalPlaces = 0, IReadOnlyList<string>? categoryIds = null) =>
        Ok(await Products.CreateAsync(_admin, NewProduct(name, unitId, sku, barcode, decimalPlaces, false, categoryIds), Guid.NewGuid().ToString(), default));

    private static ProductUpdateRequest Update(
        ProductResponse p, string? name = null, string? sku = null, string? barcode = null, long? unitId = null, int? decimalPlaces = null) =>
        new(name ?? p.Name, unitId is { } u ? WireId.Format(u) : p.UnitId, decimalPlaces ?? p.DecimalPlaces, p.SerialTracking, p.CategoryIds, p.Version,
            sku ?? p.Sku, barcode ?? p.Barcode);

    private long InsertBin(string zone, string productId, long quantityMicros, long capacityMicros, long? lowMicros = null)
    {
        var low = lowMicros?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        Execute(
            "INSERT INTO Bins (Zone, Rack, Level, Cell, Code, NormalizedCode, ProductId, QuantityMicros, CapacityMicros, LowThresholdMicros, CreatedAt, UpdatedAt) " +
            $"VALUES ('{zone}', 'R1', '01', '01', '{zone}-R1-01-01', '{zone}-R1-01-01', {productId}, {quantityMicros}, {capacityMicros}, {low}, 0, 0)");
        return Scalar("SELECT max(Id) FROM Bins");
    }

    private async Task<V2SessionPrincipal> SignInAsync(string username)
    {
        var auth = _services.GetRequiredService<V2AuthService>();
        var login = await auth.LoginAsync(new LoginRequest(username, _password), default);
        return (await auth.AuthenticateAsync(login.Value.Token, default)).Principal!;
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000));
        services.AddWmsV2Application();
        services.AddWmsV2SqlitePersistence(new V2SqliteOptions { DataSource = DatabasePath });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql) => Convert.ToInt64(Query(sql), CultureInfo.InvariantCulture);

    private string Text(string sql) => Convert.ToString(Query(sql), CultureInfo.InvariantCulture)!;

    private object Query(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
