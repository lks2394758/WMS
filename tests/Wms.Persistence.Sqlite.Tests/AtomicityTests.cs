using Microsoft.EntityFrameworkCore;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Common;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>
/// One command commits balances, ledger, audit, idempotency record and sequence together or not at all, including
/// when the database itself rejects the commit.
/// </summary>
public sealed class AtomicityTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Business_failure_on_a_later_line_stores_nothing_and_the_same_key_is_evaluated_again()
    {
        await using var host = await _database.StartAsync();
        await ReceiveAsync(host, DevelopmentData.Widget, 5);
        await ReceiveAsync(host, DevelopmentData.Gadget, 5);
        var before = await SnapshotAsync(host);
        var key = Guid.NewGuid();

        var rejected = await host.Commands.ShipAsync(
            new ShipmentRequest(DevelopmentData.ShelfA0101.Value,
                [new QuantityLine(DevelopmentData.Widget.Value, 2), new QuantityLine(DevelopmentData.Gadget.Value, 9)]),
            new CommandContext(DevelopmentData.ShippingClerk, key),
            CancellationToken.None);

        Assert.Equal(ProblemCodes.InsufficientStock, rejected.Error!.Code);
        Assert.Equal(before, await SnapshotAsync(host)); // line 1 changed the widget balance in memory only

        var retried = await host.Commands.ShipAsync(
            new ShipmentRequest(DevelopmentData.ShelfA0101.Value,
                [new QuantityLine(DevelopmentData.Widget.Value, 2), new QuantityLine(DevelopmentData.Gadget.Value, 5)]),
            new CommandContext(DevelopmentData.ShippingClerk, key),
            CancellationToken.None);
        Assert.False(retried.Value.Replayed);
        Assert.EndsWith("-000001", retried.Value.Response.TransactionNo, StringComparison.Ordinal); // no number was consumed
        Assert.Equal(3, await host.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
        Assert.Equal(0, await host.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Gadget));
    }

    [Fact]
    public async Task Database_failure_during_commit_rolls_back_every_part_and_releases_the_writer()
    {
        await using var host = await _database.StartAsync(lockTimeoutSeconds: 2);
        await ReceiveAsync(host, DevelopmentData.Widget, 5);
        Guid existingAuditId;
        await using (var db = host.CreateDb())
        {
            existingAuditId = (await db.AuditEntries.SingleAsync()).Id;
        }

        var before = await SnapshotAsync(host);
        var now = DateTimeOffset.UtcNow;

        var scope = await host.Store.BeginWriteAsync(CancellationToken.None);
        await using (scope)
        {
            var key = new BalanceKey(DevelopmentData.Warehouse, DevelopmentData.ShelfA0101, DevelopmentData.Widget);
            var balances = await scope.LockBalancesAsync([key], CancellationToken.None);
            var products = await scope.GetProductsAsync([DevelopmentData.Widget], CancellationToken.None);
            var locations = await scope.GetLocationsAsync([DevelopmentData.ShelfA0101], CancellationToken.None);
            var line = InventoryOperations.Receive(
                1, products[DevelopmentData.Widget], locations[DevelopmentData.ShelfA0101], balances[key], Quantity.ForTransaction(4).Value);
            var number = await scope.AllocateTransactionNumberAsync(InventoryTransactionType.Receipt, DateOnly.FromDateTime(now.UtcDateTime), CancellationToken.None);
            scope.Add(InventoryTransaction.Create(
                new InventoryTransactionContext(InventoryTransactionId.New(), number, DevelopmentData.ReceivingClerk, now), [line.Value]).Value);
            scope.Add(new IdempotencyRecord(DevelopmentData.ReceivingClerk, Guid.NewGuid(), "receipt", "hash", "{}", now, now.AddHours(24)));
            // Duplicate primary key: the database rejects the commit after balances, ledger and idempotency rows were sent.
            scope.Add(new AuditEntry(existingAuditId, now, DevelopmentData.ReceivingClerk, "inventory.receipt", "InventoryTransaction", number.Value));

            await Assert.ThrowsAsync<DbUpdateException>(() => scope.CommitAsync(now, CancellationToken.None));
        }

        Assert.Equal(before, await SnapshotAsync(host));

        // The writer was released: the next command commits at once and reuses the unconsumed number.
        var next = await host.Commands.ReceiveAsync(
            new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1)]),
            new CommandContext(DevelopmentData.ReceivingClerk, Guid.NewGuid()),
            CancellationToken.None);
        Assert.EndsWith("-000002", next.Value.Response.TransactionNo, StringComparison.Ordinal);
        Assert.Equal(6, await host.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
    }

    [Fact]
    public async Task Disposing_an_uncommitted_scope_rolls_back_and_releases_the_writer()
    {
        await using var host = await _database.StartAsync(lockTimeoutSeconds: 1);
        var before = await SnapshotAsync(host);

        await using (var scope = await host.Store.BeginWriteAsync(CancellationToken.None))
        {
            var key = new BalanceKey(DevelopmentData.Warehouse, DevelopmentData.Receiving, DevelopmentData.Gadget);
            await scope.LockBalancesAsync([key], CancellationToken.None);
            await scope.AllocateTransactionNumberAsync(InventoryTransactionType.Receipt, new DateOnly(2026, 10, 14), CancellationToken.None);
        }

        Assert.Equal(before, await SnapshotAsync(host));
        await using var again = await host.Store.BeginWriteAsync(CancellationToken.None); // would time out if still held
    }

    private static Task ReceiveAsync(TestHost host, ProductId product, int quantity) =>
        host.Commands.ReceiveAsync(
            new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(product.Value, quantity)]),
            new CommandContext(DevelopmentData.ReceivingClerk, Guid.NewGuid()),
            CancellationToken.None);

    /// <summary>Every table an inventory command writes, as comparable values.</summary>
    private static async Task<string> SnapshotAsync(TestHost host)
    {
        await using var db = host.CreateDb();
        var balances = await db.Balances.OrderBy(b => b.PageKey)
            .Select(b => $"{b.PageKey}={b.AvailableQuantity}@v{b.Version}")
            .ToListAsync();
        var sequences = await db.TransactionSequences.Select(s => $"{s.Type}/{s.BusinessDate}={s.LastValue}").ToListAsync();
        return string.Join(
            " | ",
            string.Join(",", balances),
            await db.InventoryTransactions.CountAsync(),
            await db.InventoryTransactionLines.CountAsync(),
            await db.AuditEntries.CountAsync(),
            await db.IdempotencyRecords.CountAsync(),
            string.Join(",", sequences.Order(StringComparer.Ordinal)));
    }
}
