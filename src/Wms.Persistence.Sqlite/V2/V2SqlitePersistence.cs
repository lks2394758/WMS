using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>
/// Settings of the v2 SQLite file (configuration section <c>V2:Sqlite</c>). Separate from the v1
/// <see cref="SqliteOptions"/>: the v2 process never opens the v1 file.
/// </summary>
public sealed class V2SqliteOptions
{
    public const string Section = "V2:Sqlite";
    public const string DefaultDataSource = "App_Data/wms-v2-demo.db";

    /// <summary>File name of the v1 demonstration database; a v2 data source with this name is refused.</summary>
    public const string LegacyFileName = "wms-demo.db";

    public string DataSource { get; set; } = DefaultDataSource;

    public int LockTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Absolute path of the v2 file. Refuses ':memory:', connection strings, a file named like the v1 database and the
    /// resolved v1 path itself (<paramref name="legacyDataSource"/>), so a configuration mistake can never point v2 at v1.
    /// </summary>
    public static string ResolveDataSource(string? dataSource, string contentRoot, string? legacyDataSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        var path = string.IsNullOrWhiteSpace(dataSource) ? DefaultDataSource : dataSource.Trim();
        if (path.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || path.Contains('=', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("V2:Sqlite:DataSource must be a file path (not ':memory:' or a connection string).");
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path));
        if (string.Equals(Path.GetFileName(full), LegacyFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"V2:Sqlite:DataSource must not be the v1 database file '{LegacyFileName}'. Use a separate file such as '{DefaultDataSource}'.");
        }

        if (!string.IsNullOrWhiteSpace(legacyDataSource))
        {
            var legacy = Path.GetFullPath(Path.IsPathRooted(legacyDataSource) ? legacyDataSource : Path.Combine(contentRoot, legacyDataSource));
            if (string.Equals(full, legacy, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("V2:Sqlite:DataSource must differ from Persistence:Sqlite:DataSource (the v1 database).");
            }
        }

        return full;
    }

    internal string BuildConnectionString()
    {
        if (!Path.IsPathRooted(DataSource))
        {
            throw new InvalidOperationException("The v2 SQLite data source must be an absolute path; resolve it first.");
        }

        if (LockTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("V2:Sqlite:LockTimeoutSeconds must be between 1 and 300.");
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = DataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = LockTimeoutSeconds,
        }.ToString();
    }
}

public static class V2SqlitePersistence
{
    /// <summary>Registers the v2 ports on the v2 file. <paramref name="options"/> must hold a resolved absolute path.</summary>
    public static IServiceCollection AddWmsV2SqlitePersistence(this IServiceCollection services, V2SqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.BuildConnectionString();

        services.AddSingleton(options);
        services.AddDbContextFactory<WmsV2DbContext>(builder => Configure(builder, connectionString));
        services.AddSingleton<V2SqliteStore>();
        services.AddSingleton<IV2IdentityStore>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2SeedStore>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2HealthProbe>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2CatalogReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2CatalogStore>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2AdminReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2InventoryReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2SerialReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2CountReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        services.AddSingleton<IV2MonitoringReader>(sp => sp.GetRequiredService<V2SqliteStore>());
        return services;
    }

    /// <summary>
    /// Creates the directory and file if needed, applies the v2 migrations (repeatable; an up-to-date file is unchanged)
    /// and switches to WAL. Touches only the v2 file.
    /// </summary>
    public static async Task<SqliteInitializationReport> InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<V2SqliteOptions>();
        var directory = Path.GetDirectoryName(options.DataSource)
            ?? throw new InvalidOperationException("The v2 SQLite data source has no directory.");
        Directory.CreateDirectory(directory);
        var existed = File.Exists(options.DataSource);

        var contexts = services.GetRequiredService<IDbContextFactory<WmsV2DbContext>>();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        return new SqliteInitializationReport(options.DataSource, !existed, pending);
    }

    internal static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString)
            // The schema is the hand-written SQL migration (V2Schema); the model maps only the tables in use, so the
            // model differ always reports differences. Schema drift is covered by V2 schema tests instead.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
}

/// <summary>Lets the local <c>dotnet ef</c> tool build the v2 model without a host. It never opens a real database.</summary>
internal sealed class DesignTimeWmsV2DbContextFactory : IDesignTimeDbContextFactory<WmsV2DbContext>
{
    public WmsV2DbContext CreateDbContext(string[] args)
    {
        var options = new V2SqliteOptions { DataSource = Path.Combine(Path.GetTempPath(), "wms-v2-design-time.db") };
        var builder = new DbContextOptionsBuilder<WmsV2DbContext>();
        V2SqlitePersistence.Configure(builder, options.BuildConnectionString());
        return new WmsV2DbContext(builder.Options);
    }
}
