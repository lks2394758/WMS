using Microsoft.Data.Sqlite;

namespace Wms.Persistence.Sqlite;

/// <summary>Settings of the SQLite demonstration provider (configuration section <c>Persistence:Sqlite</c>).</summary>
public sealed class SqliteOptions
{
    public const string Section = "Persistence:Sqlite";

    /// <summary>Default database file, relative to the host's content root.</summary>
    public const string DefaultDataSource = "App_Data/wms-demo.db";

    /// <summary>
    /// The database file. A relative path is resolved against the content root by the host (see
    /// <see cref="ResolveDataSource"/>); the provider itself always receives an absolute path.
    /// </summary>
    public string DataSource { get; set; } = DefaultDataSource;

    /// <summary>
    /// How long a write waits for the single SQLite writer lock (in-process queue plus the file lock held by another
    /// process) before it is rejected with <see cref="Abstractions.PersistenceConflictException"/>.
    /// </summary>
    public int LockTimeoutSeconds { get; set; } = 10;

    /// <summary>Absolute path of <paramref name="dataSource"/>, resolved against <paramref name="contentRoot"/>.</summary>
    public static string ResolveDataSource(string? dataSource, string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        var path = string.IsNullOrWhiteSpace(dataSource) ? DefaultDataSource : dataSource.Trim();
        if (path.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || path.Contains('=', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persistence:Sqlite:DataSource must be a file path (not ':memory:' or a connection string).");
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path));
    }

    /// <summary>The connection string for an absolute <see cref="DataSource"/>.</summary>
    internal string BuildConnectionString()
    {
        if (!Path.IsPathRooted(DataSource))
        {
            throw new InvalidOperationException("The SQLite data source must be an absolute path; resolve it first.");
        }

        if (LockTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("Persistence:Sqlite:LockTimeoutSeconds must be between 1 and 300.");
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = DataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = LockTimeoutSeconds, // busy wait for the file lock held by another connection
        }.ToString();
    }
}
