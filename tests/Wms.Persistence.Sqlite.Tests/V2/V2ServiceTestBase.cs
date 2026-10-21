using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.V2;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Persistence.Sqlite.V2;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>A controllable clock for the v2 service tests.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real v2 Application services on a temporary SQLite file, seeded with the four role accounts (a password generated
/// per run). States that later phases create through their endpoints (stock, counts, alerts) are written with SQL.
/// </summary>
public abstract class V2ServiceTestBase : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-service-tests", Guid.NewGuid().ToString("N"));

    protected string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    internal TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    protected ServiceProvider Services { get; private set; } = null!;

    protected string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    public async Task InitializeAsync()
    {
        Services = Build();
        await V2SqlitePersistence.InitializeAsync(Services);
        await Get<V2Seeder>().SeedAsync(Password, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    protected T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A new provider on the same file, as after a process restart.</summary>
    protected async Task RestartAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Services = Build();
    }

    protected async Task<(string Token, V2SessionPrincipal Principal)> LoginAsync(string username, string? password = null)
    {
        var auth = Get<V2AuthService>();
        var login = await auth.LoginAsync(new LoginRequest(username, password ?? Password), default);
        Assert.True(login.IsSuccess, login.Error?.Code);
        return (login.Value.Token, (await auth.AuthenticateAsync(login.Value.Token, default)).Principal!);
    }

    protected async Task<V2SessionPrincipal> SignInAsync(string username, string? password = null) =>
        (await LoginAsync(username, password)).Principal;

    protected Task<V2AuthenticationResult> AuthenticateAsync(string token) => Get<V2AuthService>().AuthenticateAsync(token, default);

    protected async Task<ProductResponse> CreateProductAsync(V2SessionPrincipal admin, string name, int decimalPlaces = 0) =>
        Ok(await Get<Wms.Application.V2.Catalog.V2ProductService>()
            .CreateAsync(admin, new ProductCreateRequest(name, "1", decimalPlaces, false, []), Guid.NewGuid().ToString(), default));

    protected static T Ok<T>(V2Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException($"Expected success, got {result.Failure!.Code}: {result.Failure.Message}");

    protected static V2Failure Fail<T>(V2Result<T> result) =>
        result.Failure ?? throw new Xunit.Sdk.XunitException("Expected a failure, got success.");

    protected void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    protected long Scalar(string sql) => Convert.ToInt64(Query(sql), CultureInfo.InvariantCulture);

    protected string Text(string sql) => Convert.ToString(Query(sql), CultureInfo.InvariantCulture)!;

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

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000));
        services.AddWmsV2Application();
        services.AddWmsV2SqlitePersistence(new V2SqliteOptions { DataSource = DatabasePath });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }
}
