using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.V2;
using Wms.Persistence.Abstractions.V2;
using Wms.Persistence.Sqlite.V2;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>
/// The v2 schema on an on-disk temporary file: migration, the 16 business tables, repeatable start-up, insert-missing
/// seeding, exact integer storage and the database protections. SQLite evidence only (AGENTS.md rule 4).
/// </summary>
public sealed class V2SchemaTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-schema-tests", Guid.NewGuid().ToString("N"));
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private ServiceProvider _services = null!;

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    public async Task InitializeAsync()
    {
        _services = BuildServices();
        await V2SqlitePersistence.InitializeAsync(_services);
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
    public async Task Migration_creates_exactly_the_16_business_tables()
    {
        var all = await QueryListAsync("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name");
        var diagnostic = "sqlite_master tables: " + string.Join(", ", all);

        // Everything that is not one of the 16 business tables must be a known framework technical table.
        var technical = all.Except(V2Schema.BusinessTables, StringComparer.Ordinal).ToList();
        Assert.True(technical.All(t => V2Schema.TechnicalTables.Contains(t, StringComparer.Ordinal)), diagnostic);
        Assert.True(technical.Contains("__EFMigrationsHistory", StringComparer.Ordinal), diagnostic);

        var business = all.Intersect(V2Schema.BusinessTables, StringComparer.Ordinal).ToList();
        Assert.True(business.Count == 16, diagnostic);
        Assert.Equal(V2Schema.BusinessTables.Order(StringComparer.Ordinal), business.Order(StringComparer.Ordinal));
        Assert.All(business, t => Assert.Contains("STRICT", Scalar<string>($"SELECT sql FROM sqlite_master WHERE name = '{t}'"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Initialization_and_seeding_are_repeatable_and_insert_missing_only()
    {
        var first = await SeedAsync(_password);
        var report = await V2SqlitePersistence.InitializeAsync(_services);
        var second = await SeedAsync(_password);

        Assert.Equal(4, first.Report.UsersInserted);
        Assert.True(first.Report.UnitInserted);
        Assert.True(first.Report.SettingsInserted);
        Assert.Empty(report.AppliedMigrations);
        Assert.Equal(new V2SeedReport(0, false, false), second.Report);
        Assert.Empty(second.AccountsWaitingForPassword);
        Assert.Equal(4L, Scalar<long>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM Units"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM SystemSettings"));
        Assert.Equal(0L, Scalar<long>("SELECT (SELECT COUNT(*) FROM Products) + (SELECT COUNT(*) FROM Bins) + (SELECT COUNT(*) FROM InventoryTransactions)"));
        Assert.Equal(8L, Scalar<long>("SELECT SessionHours FROM SystemSettings"));
        Assert.Equal(20_000_000L, Scalar<long>("SELECT LowThresholdMicros FROM SystemSettings"));
    }

    [Fact]
    public async Task Seed_never_resets_an_existing_password()
    {
        await SeedAsync(_password);
        var before = Scalar<string>("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'");

        await SeedAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));

        var after = Scalar<string>("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'");
        Assert.Equal(before, after);
        Assert.StartsWith("PBKDF2-SHA256$", after, StringComparison.Ordinal);
        Assert.DoesNotContain(_password, after, StringComparison.Ordinal);
        Assert.True(_services.GetRequiredService<IPasswordHasher>().Verify(_password, after));
    }

    [Fact]
    public async Task Without_a_seed_password_no_account_is_created()
    {
        var outcome = await SeedAsync(null);
        var tooShort = await SeedAsync("short");

        Assert.Equal(0, outcome.Report.UsersInserted);
        Assert.Equal(new[] { "admin", "supervisor", "receiver", "shipper" }, outcome.AccountsWaitingForPassword);
        Assert.False(outcome.PasswordRejected);
        Assert.True(tooShort.PasswordRejected);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM Units WHERE IsSystem = 1"));
    }

    [Fact]
    public async Task Quantities_are_exact_64_bit_integers_and_strict_columns_reject_other_types()
    {
        await SeedAsync(_password);
        const long max = 999_999_999_999_999_999;
        Execute(
            "INSERT INTO Products (Name, Sku, NormalizedSku, Barcode, NormalizedBarcode, UnitId, DecimalPlaces, SerialTracking, CreatedById, CreatedAt, UpdatedAt) " +
            "VALUES ('Wire', 'SKU-000001', 'SKU-000001', 'BC-000001', 'BC-000001', 1, 6, 0, 1, 0, 0)");
        Execute(
            "INSERT INTO Bins (Zone, Rack, Level, Cell, Code, NormalizedCode, ProductId, QuantityMicros, CapacityMicros, CreatedAt, UpdatedAt) " +
            $"VALUES ('A', 'R1', '01', '01', 'A-R1-01-01', 'A-R1-01-01', 1, {max}, {max}, 0, 0)");

        Assert.Equal(max, Scalar<long>("SELECT QuantityMicros FROM Bins"));
        Assert.Equal("integer", Scalar<string>("SELECT typeof(QuantityMicros) FROM Bins"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE Bins SET QuantityMicros = 1.5"));
        Assert.Throws<SqliteException>(() => Execute($"UPDATE Bins SET QuantityMicros = {max} + 1"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE Bins SET QuantityMicros = -1"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE Bins SET ProductId = NULL"));
    }

    [Fact]
    public async Task Ledger_results_item_links_and_activity_log_are_append_only()
    {
        await SeedAsync(_password);
        foreach (var table in V2Schema.AppendOnlyTables)
        {
            Assert.Equal(2L, Scalar<long>(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = '{table}' AND name IN ('TR_{table}_NoUpdate', 'TR_{table}_NoDelete')"));
        }

        Execute("INSERT INTO ActivityLogs (OccurredAt, Type, Description, Success, Subjects) VALUES (1, 'login', 'x', 1, '[]')");
        Execute(
            "INSERT INTO InventoryTransactions (Number, Type, ActorId, ActorName, ActorRole, OccurredAt, Reason, RequestKey, RequestDigest) " +
            $"VALUES ('TX-000001', 'adjustment', 1, 'A', 'systemAdministrator', 1, 'r', 'k', '{new string('0', 64)}')");

        Assert.Throws<SqliteException>(() => Execute("UPDATE ActivityLogs SET Description = 'changed'"));
        Assert.Throws<SqliteException>(() => Execute("DELETE FROM ActivityLogs"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE InventoryTransactions SET Reason = 'changed'"));
        Assert.Throws<SqliteException>(() => Execute("DELETE FROM InventoryTransactions"));
        Assert.Throws<SqliteException>(() => Execute("DELETE FROM Users"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs"));
    }

    [Fact]
    public async Task Roles_and_sessions_are_guarded_by_the_database()
    {
        await SeedAsync(_password);
        var hash = new string('A', 64);

        Assert.Throws<SqliteException>(() => Execute("UPDATE Users SET Roles = '[\"superuser\"]' WHERE Id = 1"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE Users SET Roles = '[\"shippingClerk\",\"shippingClerk\"]' WHERE Id = 1"));
        Assert.Throws<SqliteException>(() => Execute("UPDATE Users SET Roles = '[]' WHERE Id = 1"));

        // The admin account holds only systemAdministrator: another current role is refused.
        Assert.Throws<SqliteException>(() => Execute(
            $"INSERT INTO LoginSessions (UserId, CurrentRole, TokenHash, LoggedInAt, ExpiresAt) VALUES (1, 'shippingClerk', '{hash}', 1, 2)"));

        Execute($"INSERT INTO LoginSessions (UserId, CurrentRole, TokenHash, LoggedInAt, ExpiresAt) VALUES (1, 'systemAdministrator', '{hash}', 1, 2)");
        Assert.Throws<SqliteException>(() => Execute( // one open session per account
            $"INSERT INTO LoginSessions (UserId, CurrentRole, TokenHash, LoggedInAt, ExpiresAt) VALUES (1, 'systemAdministrator', '{new string('B', 64)}', 1, 2)"));

        Execute("UPDATE LoginSessions SET LoggedOutAt = 3, Version = 2");
        Assert.Throws<SqliteException>(() => Execute("UPDATE LoginSessions SET LoggedOutAt = NULL")); // ended is final
        Assert.Throws<SqliteException>(() => Execute("DELETE FROM LoginSessions"));
    }

    [Fact]
    public async Task Alerts_allow_only_open_to_resolved_and_keep_opening_evidence()
    {
        await SeedAsync(_password);
        InsertProductAndBin();
        Execute(
            "INSERT INTO Alerts (Type, BinId, ProductId, Message, TriggerSnapshot, CreatedAt, Status) " +
            "VALUES ('lowStock', 1, 1, 'A-R1-01-01 below 20%', '{\"quantity\":\"0\"}', 1, 'open')");

        AssertRaises("UPDATE Alerts SET Message = 'rewritten'", "Alerts");
        AssertRaises("UPDATE Alerts SET TriggerSnapshot = '{}'", "Alerts");
        AssertRaises("UPDATE Alerts SET CountTaskId = 1", "Alerts"); // NULL -> value is a change (IS NOT)
        AssertRaises("UPDATE Alerts SET CreatedAt = 2", "Alerts");
        AssertRaises("UPDATE Alerts SET Version = 2", "Alerts"); // open -> open is not a transition

        Execute("UPDATE Alerts SET Status = 'resolved', ResolvedAt = 5, ResolutionNote = 'back to normal', Version = 2");

        AssertRaises("UPDATE Alerts SET ResolutionNote = 'changed'", "Alerts");
        AssertRaises("UPDATE Alerts SET Status = 'open', ResolvedAt = NULL", "Alerts");
        AssertRaises("DELETE FROM Alerts", "Alerts");
        Assert.Equal("A-R1-01-01 below 20%", Scalar<string>("SELECT Message FROM Alerts"));
    }

    [Fact]
    public async Task Creation_metadata_of_products_and_count_tasks_is_immutable()
    {
        await SeedAsync(_password);
        InsertProductAndBin(creationKey: "6f1c0a3e-1111-4c2b-9d1a-000000000001");
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound, " +
            "CancelledAt, CancelReason, CreationRequestKey, CreationRequestDigest) VALUES ('CT-000001', 1, 'A-R1-01-01', 1, 'Admin', " +
            $"'systemAdministrator', 1, 'cancelled', 1, 2, 'test', '6f1c0a3e-1111-4c2b-9d1a-000000000002', '{new string('b', 64)}')");

        AssertRaises("UPDATE Products SET CreationRequestKey = 'other'", "creation metadata");
        AssertRaises($"UPDATE Products SET CreationRequestDigest = '{new string('c', 64)}'", "creation metadata");
        AssertRaises("UPDATE Products SET CreatedById = 2", "creation metadata");
        AssertRaises("UPDATE Products SET CreationRequestKey = NULL, CreationRequestDigest = NULL", "creation metadata");
        Execute("UPDATE Products SET Name = 'Renamed', Version = Version + 1"); // normal edits still work

        AssertRaises("UPDATE CountTasks SET CreatorId = 2", "creation metadata");
        AssertRaises("UPDATE CountTasks SET CreationRequestKey = 'other'", "creation metadata");
        AssertRaises("UPDATE CountTasks SET BinCode = 'B-R1-01-01'", "creation metadata");
        Execute("UPDATE CountTasks SET CancelReason = 'corrected reason', Version = Version + 1");
    }

    private void InsertProductAndBin(string? creationKey = null)
    {
        var request = creationKey is null ? "NULL, NULL" : $"'{creationKey}', '{new string('a', 64)}'";
        Execute(
            "INSERT INTO Products (Name, Sku, NormalizedSku, Barcode, NormalizedBarcode, UnitId, DecimalPlaces, SerialTracking, CreatedById, " +
            $"CreationRequestKey, CreationRequestDigest, CreatedAt, UpdatedAt) VALUES ('Wire', 'SKU-000001', 'SKU-000001', 'BC-000001', 'BC-000001', 1, 2, 0, 1, {request}, 0, 0)");
        Execute(
            "INSERT INTO Bins (Zone, Rack, Level, Cell, Code, NormalizedCode, ProductId, CapacityMicros, CreatedAt, UpdatedAt) " +
            "VALUES ('A', 'R1', '01', '01', 'A-R1-01-01', 'A-R1-01-01', 1, 200000000, 0, 0)");
    }

    private void AssertRaises(string sql, string messageFragment)
    {
        var error = Assert.Throws<SqliteException>(() => Execute(sql));
        Assert.Contains(messageFragment, error.Message, StringComparison.Ordinal);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000)); // fast; the format is the real one
        services.AddWmsV2Application();
        services.AddWmsV2SqlitePersistence(new V2SqliteOptions { DataSource = DatabasePath });
        return services.BuildServiceProvider();
    }

    private async Task<V2SeedOutcome> SeedAsync(string? password)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<V2Seeder>().SeedAsync(password, CancellationToken.None);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> QueryListAsync(string sql)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
