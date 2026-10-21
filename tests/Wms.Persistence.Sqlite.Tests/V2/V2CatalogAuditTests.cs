using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Wms.Persistence.Abstractions.V2;
using Wms.Persistence.Sqlite.V2;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>
/// Review fixes of B2a: time and identity are taken inside the write transaction (after the writer queue), validation
/// rejections are audited with the verified identity, product names search Unicode case-insensitively before paging,
/// and create retries stay safe after archiving and with a missing category list.
/// </summary>
public sealed class V2CatalogAuditTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-catalog-audit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    private ServiceProvider _services = null!;

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private V2AuthService Auth => _services.GetRequiredService<V2AuthService>();

    private V2ProductService Products => _services.GetRequiredService<V2ProductService>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000));
        services.AddWmsV2Application();
        services.AddWmsV2SqlitePersistence(new V2SqliteOptions { DataSource = DatabasePath });
        _services = services.BuildServiceProvider();
        await V2SqlitePersistence.InitializeAsync(_services);
        await _services.GetRequiredService<V2Seeder>().SeedAsync(_password, CancellationToken.None);
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

    [Fact]
    public async Task A_session_that_expires_while_waiting_for_the_writer_cannot_write()
    {
        var admin = await SignInAsync("admin"); // expires at 17:00
        var units = UnitsWith(new HookedStore(_services.GetRequiredService<IV2CatalogStore>(), () =>
        {
            _clock.Advance(TimeSpan.FromHours(8)); // the request waited in the writer queue past ExpiresAt
            return Task.CompletedTask;
        }));

        var result = await units.CreateAsync(admin, new UnitWriteRequest("Box"), default);

        Assert.Equal(ProblemCodesV2.SessionExpired, result.Failure!.Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Units WHERE NormalizedName = 'BOX'"));
        Assert.Equal(_clock.GetUtcNow().UtcTicks, Scalar("SELECT OccurredAt FROM ActivityLogs WHERE Type = 'unitCreate'"));
        Assert.Equal(admin.UserId, Scalar("SELECT ActorId FROM ActivityLogs WHERE Type = 'unitCreate'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitCreate' AND ActorRole IS NULL AND Success = 0")); // no role invented
    }

    [Fact]
    public async Task A_role_switch_while_waiting_is_authorized_and_logged_with_the_new_role()
    {
        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\",\"receivingClerk\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        var supervisor = await SignInAsync("supervisor");
        Assert.Equal(RoleNames.WarehouseSupervisor, supervisor.CurrentRole);
        var units = UnitsWith(new HookedStore(_services.GetRequiredService<IV2CatalogStore>(), async () =>
        {
            // Another tab of the same session switches to the receiving role before this write gets the lock.
            var switched = await Auth.SwitchRoleAsync(supervisor, new SwitchRoleRequest(RoleNames.ReceivingClerk), default);
            Assert.True(switched.IsSuccess);
        }));

        var result = await units.CreateAsync(supervisor, new UnitWriteRequest("Box"), default);

        Assert.Equal(ProblemCodesV2.Forbidden, result.Failure!.Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Units WHERE NormalizedName = 'BOX'"));
        Assert.Equal("receivingClerk", Text("SELECT ActorRole FROM ActivityLogs WHERE Type = 'unitCreate' AND Success = 0"));
        Assert.Equal("仓库主管", Text("SELECT ActorName FROM ActivityLogs WHERE Type = 'unitCreate' AND Success = 0"));
    }

    [Fact]
    public async Task Validation_rejections_of_writes_are_audited_without_request_content()
    {
        var admin = await SignInAsync("admin");
        var svg = Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        var units = _services.GetRequiredService<V2UnitService>();
        var categories = _services.GetRequiredService<V2CategoryService>();

        Assert.Equal(ProblemCodesV2.ValidationFailed, (await units.CreateAsync(admin, new UnitWriteRequest("   "), default)).Failure!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, (await units.UpdateAsync(admin, "x", new UnitWriteRequest("Box", 1), default)).Failure!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, (await categories.UpdateAsync(admin, "1", new CategoryWriteRequest("A"), default)).Failure!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, (await Products.CreateAsync(admin, Product("A"), null, default)).Failure!.Code);
        Assert.Equal(
            ProblemCodesV2.InvalidImage,
            (await Products.CreateAsync(admin, Product("B") with { ImageBase64 = svg, ImageContentType = "image/png" }, Guid.NewGuid().ToString(), default)).Failure!.Code);
        Assert.Equal(ProblemCodesV2.ValidationFailed, (await Products.ArchiveAsync(admin, "0", null, default)).Failure!.Code);

        Assert.Equal(6L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Success = 0 AND ActorId = 1 AND ActorRole = 'systemAdministrator'"));
        Assert.Equal(5L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Success = 0 AND FailureCode = 'VALIDATION_FAILED'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Success = 0 AND FailureCode = 'INVALID_IMAGE' AND Type = 'productCreate'"));
        var logs = Text("SELECT group_concat(Description || '|' || coalesce(FailureReason, '') || '|' || coalesce(Changes, ''), '\n') FROM ActivityLogs");
        Assert.DoesNotContain(svg, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", logs, StringComparison.Ordinal);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Units WHERE IsSystem = 0") + Scalar("SELECT COUNT(*) FROM Products"));
    }

    [Fact]
    public async Task Product_names_search_unicode_case_insensitively_in_both_directions_before_paging()
    {
        var admin = await SignInAsync("admin");
        foreach (var name in new[] { "écrou A", "Bolt", "ÉCROU B", "Nut", "écrou C", "ÉTAU" })
        {
            Assert.True((await Products.CreateAsync(admin, Product(name), Guid.NewGuid().ToString(), default)).IsSuccess);
        }

        var first = (await Products.ListAsync("Écrou", null, null, 2, default)).Value;
        Assert.Equal(new[] { "écrou A", "ÉCROU B" }, first.Items.Select(p => p.Name));
        Assert.NotNull(first.NextCursor);
        var second = (await Products.ListAsync("Écrou", null, first.NextCursor, 2, default)).Value;
        Assert.Equal(new[] { "écrou C" }, second.Items.Select(p => p.Name));
        Assert.Null(second.NextCursor);

        Assert.Equal("ÉTAU", Assert.Single((await Products.ListAsync("étau", null, null, null, default)).Value.Items).Name);
        Assert.Equal(3, (await Products.ListAsync("ÉCROU", null, null, null, default)).Value.Items.Count);
        Assert.Single((await Products.ListAsync("sku-000002", null, null, null, default)).Value.Items); // normalized SKU path unchanged
    }

    [Fact]
    public async Task Retrying_a_create_after_the_product_was_archived_creates_nothing()
    {
        var admin = await SignInAsync("admin");
        var key = Guid.NewGuid().ToString();
        var created = (await Products.CreateAsync(admin, Product("Old"), key, default)).Value;
        Assert.True((await Products.ArchiveAsync(admin, created.Id, null, default)).IsSuccess);
        var counter = Scalar("SELECT LastSkuNumber FROM SystemSettings");

        var retry = await Products.CreateAsync(admin, Product("Old"), key, default);

        Assert.True(retry.Replayed);
        Assert.Equal(created.Id, retry.Value.Id);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Products"));
        Assert.Equal(counter, Scalar("SELECT LastSkuNumber FROM SystemSettings"));
    }

    [Fact]
    public async Task A_missing_category_list_is_an_empty_list_for_direct_callers()
    {
        var admin = await SignInAsync("admin");
        var key = Guid.NewGuid().ToString();

        var created = await Products.CreateAsync(admin, Product("A") with { CategoryIds = null! }, key, default);
        var replay = await Products.CreateAsync(admin, Product("A"), key, default); // [] is the same body

        Assert.True(created.IsSuccess);
        Assert.Empty(created.Value.CategoryIds);
        Assert.True(replay.Replayed);
    }

    private static ProductCreateRequest Product(string name) => new(name, "1", 0, false, []);

    private V2UnitService UnitsWith(IV2CatalogStore store) =>
        new(
            _services.GetRequiredService<IV2CatalogReader>(),
            new V2CatalogWriter(store, _services.GetRequiredService<IV2IdentityStore>(), _clock));

    private async Task<V2SessionPrincipal> SignInAsync(string username)
    {
        var login = await Auth.LoginAsync(new LoginRequest(username, _password), default);
        return (await Auth.AuthenticateAsync(login.Value.Token, default)).Principal!;
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

    /// <summary>Runs <paramref name="whileQueued"/> just before the real write transaction is opened.</summary>
    private sealed class HookedStore(IV2CatalogStore inner, Func<Task> whileQueued) : IV2CatalogStore
    {
        public async Task<IV2CatalogWriteScope> BeginWriteAsync(CancellationToken cancellationToken)
        {
            await whileQueued();
            return await inner.BeginWriteAsync(cancellationToken);
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
