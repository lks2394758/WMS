namespace Wms.Persistence.Sqlite.V2;

/// <summary>
/// Names of the v2 schema used by the model and the tests. The schema itself is created by migrations only; the first
/// one is <see cref="Migrations.InitialV2Schema"/> with its frozen SQL (<see cref="Migrations.InitialV2SchemaSql"/>).
/// Changing these lists never changes an applied migration.
/// </summary>
internal static class V2Schema
{
    /// <summary>The 16 approved business tables (docs/database-redesign-decisions.zh-CN.md §10).</summary>
    public static IReadOnlyList<string> BusinessTables { get; } =
    [
        "Users", "LoginSessions", "SystemSettings", "Units", "Categories", "Products", "ProductCategories", "Bins",
        "CountTasks", "CountResults", "InventoryTransactions", "InventoryTransactionLines", "SerializedItems",
        "TransactionLineItems", "ActivityLogs", "Alerts",
    ];

    /// <summary>
    /// Framework technical tables that may exist next to the business tables: EF Core's migration history and migration
    /// lock, and SQLite's AUTOINCREMENT counter table. No business data lives in them.
    /// </summary>
    public static IReadOnlyList<string> TechnicalTables { get; } = ["__EFMigrationsHistory", "__EFMigrationsLock", "sqlite_sequence"];

    /// <summary>Success ledger, its lines and item links, submitted count results and the activity log: insert only.</summary>
    public static IReadOnlyList<string> AppendOnlyTables { get; } =
        ["InventoryTransactions", "InventoryTransactionLines", "TransactionLineItems", "CountResults", "ActivityLogs"];

    public static string AppendOnlyTriggerName(string table, string operation) =>
        $"TR_{table}_No{string.Concat(operation[..1].ToUpperInvariant(), operation[1..].ToLowerInvariant())}";

    public static string NoDeleteTriggerName(string table) => $"TR_{table}_NoDelete";
}
