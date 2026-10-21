using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite;

/// <summary>What <see cref="SqlitePersistence.InitializeAsync"/> did to the database file.</summary>
public sealed record SqliteInitializationReport(string DataSource, bool FileCreated, IReadOnlyList<string> AppliedMigrations);

public static class SqlitePersistence
{
    /// <summary>
    /// Registers the SQLite demonstration provider for every persistence port. <paramref name="options"/> must hold an
    /// absolute <see cref="SqliteOptions.DataSource"/> (see <see cref="SqliteOptions.ResolveDataSource"/>). One store
    /// instance per container: its in-process writer queue covers every write of this process.
    /// </summary>
    public static IServiceCollection AddWmsSqlitePersistence(this IServiceCollection services, SqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.BuildConnectionString();

        services.AddSingleton(options);
        services.AddDbContextFactory<WmsDbContext>(builder => builder.UseSqlite(connectionString));
        services.AddSingleton<SqliteWmsStore>();
        services.AddSingleton<ICatalogReader>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<IInventoryReader>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<IInventoryStore>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<SqliteWmsStore>());
        services.AddSingleton<ISeedStore>(sp => sp.GetRequiredService<SqliteWmsStore>());
        return services;
    }

    /// <summary>
    /// Creates the directory and the file if needed, applies every pending EF Core migration (repeatable: an
    /// up-to-date file is left unchanged; EF Core's migration lock serializes concurrent starts) and switches the file to
    /// WAL journaling, so readers never wait for the writer. Run once at startup before serving requests.
    /// </summary>
    public static async Task<SqliteInitializationReport> InitializeAsync(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<SqliteOptions>();
        var directory = Path.GetDirectoryName(options.DataSource)
            ?? throw new InvalidOperationException("The SQLite data source has no directory.");
        Directory.CreateDirectory(directory);
        var existed = File.Exists(options.DataSource);

        var contexts = services.GetRequiredService<IDbContextFactory<WmsDbContext>>();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        return new SqliteInitializationReport(options.DataSource, !existed, pending);
    }
}

/// <summary>Lets the local <c>dotnet ef</c> tool build the model without a host. It never opens the file.</summary>
internal sealed class DesignTimeWmsDbContextFactory : IDesignTimeDbContextFactory<WmsDbContext>
{
    public WmsDbContext CreateDbContext(string[] args)
    {
        var options = new SqliteOptions { DataSource = Path.Combine(Path.GetTempPath(), "wms-design-time.db") };
        return new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseSqlite(options.BuildConnectionString()).Options);
    }
}
