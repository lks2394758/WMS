using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.Inventory;
using Wms.Application.Persistence.InMemory;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>One on-disk database file in its own temporary directory, deleted after the test.</summary>
internal sealed class TestDatabase : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wms-sqlite-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The directory does not exist before the first start: initialization must create it.</summary>
    public string FilePath => Path.Combine(Root, "App_Data", "wms-demo.db");

    public Task<TestHost> StartAsync(
        string? password = null, int lockTimeoutSeconds = 30, TimeProvider? clock = null, bool usePassword = true) =>
        TestHost.StartAsync(FilePath, usePassword ? password ?? TestHost.Password : null, lockTimeoutSeconds, clock);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a pooled connection of a parallel test; the OS temp cleanup removes it later.
        }
    }
}

/// <summary>
/// One "process": its own service container, store instance (in-process writer queue) and connection pool usage,
/// on a shared file. Disposing it and starting another on the same file is a restart.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    /// <summary>Generated per run: no password is stored in the repository.</summary>
    public static readonly string Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;

    private TestHost(ServiceProvider services)
    {
        _services = services;
        _scope = services.CreateAsyncScope();
    }

    public SqliteInitializationReport Initialization { get; private set; } = null!;
    public DevelopmentSeedReport Seed { get; private set; } = null!;

    public InventoryCommandService Commands => _scope.ServiceProvider.GetRequiredService<InventoryCommandService>();
    public AuthService Auth => _scope.ServiceProvider.GetRequiredService<AuthService>();
    public IInventoryReader Inventory => _services.GetRequiredService<IInventoryReader>();
    public IInventoryStore Store => _services.GetRequiredService<IInventoryStore>();
    public ICatalogReader Catalog => _services.GetRequiredService<ICatalogReader>();

    public static async Task<TestHost> StartAsync(string path, string? password, int lockTimeoutSeconds, TimeProvider? clock)
    {
        var services = new ServiceCollection();
        if (clock is not null)
        {
            services.AddSingleton<TimeProvider>(clock);
        }

        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000)); // fast; format unchanged
        services.AddSingleton<IAccessTokenIssuer, FakeAccessTokenIssuer>();
        services.AddWmsApplication();
        services.AddWmsSqlitePersistence(new SqliteOptions { DataSource = path, LockTimeoutSeconds = lockTimeoutSeconds });

        var host = new TestHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
        host.Initialization = await SqlitePersistence.InitializeAsync(host._services);
        host.Seed = await DevelopmentData.SeedAsync(
            host._services.GetRequiredService<ISeedStore>(),
            host._services.GetRequiredService<IPasswordHasher>(),
            password,
            CancellationToken.None);
        return host;
    }

    public WmsDbContext CreateDb() => _services.GetRequiredService<IDbContextFactory<WmsDbContext>>().CreateDbContext();

    /// <summary>Runs one scalar statement on a fresh connection to the same file (inspection only).</summary>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var db = CreateDb();
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public async Task<int> AvailableAsync(LocationId location, ProductId product)
    {
        var page = await Inventory.ListBalancesAsync(new BalanceFilter(product, location), new PageRequest(null, 10), CancellationToken.None);
        return page.Items.SingleOrDefault()?.AvailableQuantity ?? 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}

internal sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
{
    public AccessToken Issue(UserAccount user, DateTimeOffset now) => new($"access-{user.Id}", now.AddMinutes(15));
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
