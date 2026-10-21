using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.Persistence.InMemory;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Sqlite;

namespace Wms.Api.Persistence;

/// <summary>Configuration section <c>Persistence</c>: which provider backs the persistence ports.</summary>
public sealed class PersistenceSettings
{
    public const string Section = "Persistence";
    public const string Sqlite = "Sqlite";
    public const string InMemory = "InMemory";

    /// <summary><see cref="Sqlite"/> (default, the demonstration database) or <see cref="InMemory"/> (tests only).</summary>
    public string Provider { get; set; } = Sqlite;
}

/// <summary>
/// Selects and prepares the persistence provider. Use cases only see the ports, so switching provider is a
/// configuration change here plus a provider project; nothing in Application or Domain changes.
/// </summary>
internal static class PersistenceSetup
{
    public static string AddWmsPersistence(this WebApplicationBuilder builder)
    {
        var provider = builder.Configuration.GetSection(PersistenceSettings.Section).Get<PersistenceSettings>()?.Provider
            ?? PersistenceSettings.Sqlite;

        if (string.Equals(provider, PersistenceSettings.Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            var options = builder.Configuration.GetSection(SqliteOptions.Section).Get<SqliteOptions>() ?? new SqliteOptions();
            options.DataSource = SqliteOptions.ResolveDataSource(options.DataSource, builder.Environment.ContentRootPath);
            builder.Services.AddWmsSqlitePersistence(options);
            return PersistenceSettings.Sqlite;
        }

        if (string.Equals(provider, PersistenceSettings.InMemory, StringComparison.OrdinalIgnoreCase))
        {
            // Loses every committed transaction on restart (AGENTS.md rule 5): never outside Development.
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("Persistence:Provider 'InMemory' is allowed in Development only.");
            }

            builder.Services.AddWmsInMemoryPersistence();
            return PersistenceSettings.InMemory;
        }

        throw new InvalidOperationException(
            $"Unknown Persistence:Provider '{provider}'. Use '{PersistenceSettings.Sqlite}' or '{PersistenceSettings.InMemory}'.");
    }

    /// <summary>
    /// Runs before the first request: applies pending SQLite migrations, then seeds the development catalog and users
    /// (insert-missing only, see <see cref="DevelopmentData"/>). The seed password comes from configuration
    /// (user-secrets or <c>DevelopmentSeed__Password</c>), never from a committed file.
    /// </summary>
    public static async Task InitializeWmsPersistenceAsync(this WebApplication app, string provider)
    {
        if (provider == PersistenceSettings.Sqlite)
        {
            var report = await SqlitePersistence.InitializeAsync(app.Services);
            app.Logger.LogInformation(
                "SQLite demonstration database {DataSource}: file created {FileCreated}, migrations applied {AppliedMigrations}.",
                report.DataSource,
                report.FileCreated,
                report.AppliedMigrations.Count == 0 ? "none (up to date)" : string.Join(", ", report.AppliedMigrations));
            app.Logger.LogWarning(
                "SQLite is the demonstration provider: one writer at a time on one file. It is not the production database.");
        }

        if (!app.Configuration.GetValue("DevelopmentSeed:Enabled", true))
        {
            app.Logger.LogInformation("DevelopmentSeed:Enabled is false; no catalog or users were seeded.");
            return;
        }

        var seed = await DevelopmentData.SeedAsync(
            app.Services.GetRequiredService<ISeedStore>(),
            app.Services.GetRequiredService<IPasswordHasher>(),
            app.Configuration["DevelopmentSeed:Password"],
            CancellationToken.None);
        app.Logger.LogInformation(
            "Development seed: {RecordsInserted} records inserted, {PasswordsSet} seeded users received the configured password.",
            seed.RecordsInserted,
            seed.PasswordsSet);
        if (!seed.PasswordConfigured)
        {
            app.Logger.LogWarning(
                "DevelopmentSeed:Password is not configured; seeded users created without a password cannot sign in. Set it with dotnet user-secrets.");
        }
    }
}
