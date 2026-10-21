using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Inventory;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>
/// The schema rejects bad data even when the application layer is bypassed: non-negative stock, case-insensitive
/// unique SKU and username, unique transaction numbers, foreign keys, the append-only ledger and audit trail, and the
/// balance concurrency token.
/// </summary>
public sealed class ConstraintTests : IAsyncLifetime, IDisposable
{
    private readonly TestDatabase _database = new();
    private TestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await _database.StartAsync();
        var receipt = await _host.Commands.ReceiveAsync(
            new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 3)]),
            new CommandContext(DevelopmentData.ReceivingClerk, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(receipt.IsSuccess);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Negative_balance_is_rejected_by_a_check_constraint()
    {
        await using var db = _host.CreateDb();
        var balance = await db.Balances.SingleAsync();
        balance.AvailableQuantity = -1;

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_InventoryBalances_NonNegative", error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sku_is_unique_ignoring_case()
    {
        await using var db = _host.CreateDb();
        db.Products.Add(new ProductRow { Id = Guid.NewGuid(), Sku = "widget-001", NormalizedSku = "WIDGET-001", Name = "Copy", Unit = "EA", IsActive = true });

        await AssertUniqueViolationAsync(db);
    }

    [Fact]
    public async Task Username_is_unique_ignoring_case()
    {
        await using var db = _host.CreateDb();
        db.Users.Add(new UserRow
        {
            Id = Guid.NewGuid(),
            Username = "Admin",
            NormalizedUsername = "ADMIN",
            DisplayName = "Copy",
            Role = 1,
            PasswordHash = "x",
            IsActive = true,
        });

        await AssertUniqueViolationAsync(db);
    }

    [Fact]
    public async Task Transaction_number_is_unique()
    {
        await using var db = _host.CreateDb();
        var existing = await db.InventoryTransactions.AsNoTracking().SingleAsync();
        db.InventoryTransactions.Add(new InventoryTransactionRow
        {
            Id = Guid.NewGuid(),
            Number = existing.Number,
            Type = existing.Type,
            ActorId = existing.ActorId,
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await AssertUniqueViolationAsync(db);
    }

    [Fact]
    public async Task Balance_for_an_unknown_product_violates_a_foreign_key()
    {
        await using var db = _host.CreateDb();
        var product = Guid.NewGuid();
        db.Balances.Add(new BalanceRow
        {
            WarehouseId = DevelopmentData.Warehouse.Value,
            LocationId = DevelopmentData.ShelfA0101.Value,
            ProductId = product,
            PageKey = BalanceRow.PageKeyOf(DevelopmentData.ShelfA0101.Value, product),
            AvailableQuantity = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.Contains("FOREIGN KEY", error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE \"InventoryTransactions\" SET \"Reason\" = 'edited'")]
    [InlineData("DELETE FROM \"InventoryTransactions\"")]
    [InlineData("UPDATE \"InventoryTransactionLines\" SET \"Quantity\" = 99")]
    [InlineData("DELETE FROM \"InventoryTransactionLines\"")]
    [InlineData("UPDATE \"AuditEntries\" SET \"Action\" = 'edited'")]
    [InlineData("DELETE FROM \"AuditEntries\"")]
    public async Task Ledger_and_audit_trail_are_append_only(string statement)
    {
        await using var db = _host.CreateDb();

        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(statement));

        Assert.Contains("is append-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_balance_version_is_rejected_by_the_concurrency_token()
    {
        await using var first = _host.CreateDb();
        await using var second = _host.CreateDb();
        var a = await first.Balances.SingleAsync();
        var b = await second.Balances.SingleAsync();

        a.AvailableQuantity = 2;
        a.Version++;
        await first.SaveChangesAsync();

        b.AvailableQuantity = 1;
        b.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static async Task AssertUniqueViolationAsync(WmsDbContext db)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UNIQUE constraint failed", error.InnerException!.Message, StringComparison.Ordinal);
    }
}
