using Microsoft.EntityFrameworkCore;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>Startup on a real file: directory creation, versioned migrations, WAL and idempotent seeding.</summary>
public sealed class MigrationAndSeedTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task First_start_creates_the_directory_applies_the_migrations_and_seeds_the_catalog_and_users()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(_database.FilePath)));

        await using var host = await _database.StartAsync();

        Assert.True(File.Exists(_database.FilePath));
        Assert.True(host.Initialization.FileCreated);
        Assert.Contains(host.Initialization.AppliedMigrations, m => m.EndsWith("_InitialSqliteSchema", StringComparison.Ordinal));
        Assert.Equal("wal", await host.ScalarAsync("PRAGMA journal_mode;"));
        await using (var db = host.CreateDb())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges()); // the committed migration matches the model
            Assert.Equal(0, await db.Balances.CountAsync()); // stock is never seeded
        }

        Assert.Equal(3 + 2 + 2 + 4, host.Seed.RecordsInserted); // locations, products, barcodes, users
        Assert.Equal(0, host.Seed.PasswordsSet);
        Assert.True((await host.Auth.LoginAsync(new LoginRequest("receiver", TestHost.Password), CancellationToken.None)).IsSuccess);
        var products = await host.Catalog.ListProductsAsync(new Abstractions.PageRequest(null, 10), CancellationToken.None);
        Assert.Equal(["GADGET-001", "WIDGET-001"], products.Items.Select(p => p.Sku));
    }

    [Fact]
    public async Task Restart_is_repeatable_and_never_resets_balances_passwords_or_changed_rows()
    {
        await using (var first = await _database.StartAsync())
        {
            var receipt = await first.Commands.ReceiveAsync(
                new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 9)]),
                new CommandContext(DevelopmentData.ReceivingClerk, Guid.NewGuid()),
                CancellationToken.None);
            Assert.True(receipt.IsSuccess);

            await using var db = first.CreateDb();
            var widget = await db.Products.SingleAsync(p => p.Id == DevelopmentData.Widget.Value);
            widget.Name = "Widget (renamed in the demo)";
            await db.SaveChangesAsync();
        }

        await using var second = await _database.StartAsync(password: "a-different-password-on-restart");

        Assert.False(second.Initialization.FileCreated);
        Assert.Empty(second.Initialization.AppliedMigrations);
        Assert.Equal(0, second.Seed.RecordsInserted);
        Assert.Equal(0, second.Seed.PasswordsSet);
        Assert.Equal(9, await second.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
        Assert.Equal("Widget (renamed in the demo)", (await second.Catalog.FindProductAsync(DevelopmentData.Widget, CancellationToken.None))!.Name);
        Assert.True((await second.Auth.LoginAsync(new LoginRequest("receiver", TestHost.Password), CancellationToken.None)).IsSuccess);
        var other = await second.Auth.LoginAsync(new LoginRequest("receiver", "a-different-password-on-restart"), CancellationToken.None);
        Assert.Equal(ProblemCodes.Unauthenticated, other.Error!.Code);
    }

    [Fact]
    public async Task Users_seeded_without_a_password_receive_it_once_it_is_configured_and_only_then()
    {
        await using (var first = await _database.StartAsync(usePassword: false))
        {
            Assert.False(first.Seed.PasswordConfigured);
            var denied = await first.Auth.LoginAsync(new LoginRequest("admin", TestHost.Password), CancellationToken.None);
            Assert.Equal(ProblemCodes.Unauthenticated, denied.Error!.Code);
        }

        await using (var second = await _database.StartAsync())
        {
            Assert.Equal(0, second.Seed.RecordsInserted);
            Assert.Equal(DevelopmentData.UserIds.Count, second.Seed.PasswordsSet);
            Assert.True((await second.Auth.LoginAsync(new LoginRequest("admin", TestHost.Password), CancellationToken.None)).IsSuccess);
        }

        await using var third = await _database.StartAsync(password: "changed-later");
        Assert.Equal(0, third.Seed.PasswordsSet); // a real password is never replaced by seeding
        Assert.True((await third.Auth.LoginAsync(new LoginRequest("admin", TestHost.Password), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Concurrent_first_starts_on_one_file_migrate_and_seed_once()
    {
        var hosts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => _database.StartAsync())));
        try
        {
            Assert.Equal(11, hosts.Sum(h => h.Seed.RecordsInserted));
            await using var db = hosts[0].CreateDb();
            Assert.Equal(4, await db.Users.CountAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData("App_Data/wms-demo.db", "App_Data/wms-demo.db")]
    [InlineData(null, "App_Data/wms-demo.db")]
    [InlineData("data/other.db", "data/other.db")]
    public void Relative_data_source_resolves_against_the_content_root(string? configured, string expectedRelative)
    {
        var root = Path.Combine(Path.GetTempPath(), "content-root");

        var resolved = SqliteOptions.ResolveDataSource(configured, root);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, expectedRelative)), resolved);
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("Data Source=x.db")]
    public void Data_source_must_be_a_file_path(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => SqliteOptions.ResolveDataSource(configured, Path.GetTempPath()));
    }

    [Fact]
    public void Lock_timeout_outside_the_allowed_range_is_rejected()
    {
        var options = new SqliteOptions { DataSource = Path.Combine(Path.GetTempPath(), "x.db"), LockTimeoutSeconds = 0 };

        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddWmsSqlitePersistence(options));
    }
}
