using Microsoft.EntityFrameworkCore;
using Wms.Application.Auth;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Inventory;
using Wms.Persistence.Abstractions;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>Everything committed by one host is read back, unchanged, by a new host on the same file.</summary>
public sealed class RestartPersistenceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Catalog_balances_ledger_audit_and_sequences_survive_a_restart()
    {
        var actor = DevelopmentData.Supervisor;
        await using (var first = await _database.StartAsync())
        {
            Assert.True((await first.Commands.ReceiveAsync(
                new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 10)], "PO-1"),
                new CommandContext(actor, Guid.NewGuid()),
                CancellationToken.None)).IsSuccess);
            Assert.True((await first.Commands.MoveAsync(
                new MovementRequest(DevelopmentData.ShelfA0101.Value, DevelopmentData.ShelfA0102.Value, [new QuantityLine(DevelopmentData.Widget.Value, 4)]),
                new CommandContext(actor, Guid.NewGuid()),
                CancellationToken.None)).IsSuccess);
            Assert.True((await first.Commands.ShipAsync(
                new ShipmentRequest(DevelopmentData.ShelfA0102.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1)]),
                new CommandContext(actor, Guid.NewGuid()),
                CancellationToken.None)).IsSuccess);
        }

        await using var second = await _database.StartAsync();

        // Catalog, with barcodes.
        var widget = await second.Catalog.FindProductAsync(DevelopmentData.Widget, CancellationToken.None);
        Assert.Equal(("WIDGET-001", 100, true), (widget!.Sku, widget.Capacity, widget.IsActive));
        var barcodes = await second.Catalog.GetActiveBarcodesAsync([DevelopmentData.Widget], CancellationToken.None);
        Assert.Equal(["4006381333931"], barcodes[DevelopmentData.Widget]);
        Assert.Equal(DevelopmentData.Gadget, Assert.Single(await second.Catalog.FindActiveBarcodesAsync("9780201379624", CancellationToken.None)).ProductId);

        // Balances.
        Assert.Equal(6, await second.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
        Assert.Equal(3, await second.AvailableAsync(DevelopmentData.ShelfA0102, DevelopmentData.Widget));

        // Ledger with before/after snapshots, audit entries.
        await using (var db = second.CreateDb())
        {
            var ledger = await db.InventoryTransactions.Include(t => t.Lines).OrderBy(t => t.OccurredAt).ToListAsync();
            Assert.Equal(
                [(int)InventoryTransactionType.Receipt, (int)InventoryTransactionType.Movement, (int)InventoryTransactionType.Shipment],
                ledger.Select(t => t.Type));
            Assert.Equal("PO-1", ledger[0].ReferenceNo);
            var move = Assert.Single(ledger[1].Lines);
            Assert.Equal((10, 6, 0, 4), (move.FromBefore, move.FromAfter, move.ToBefore, move.ToAfter));
            Assert.All(ledger, t => Assert.Equal(actor.Value, t.ActorId));
            Assert.Equal(
                ["inventory.receipt", "inventory.movement", "inventory.shipment"],
                await db.AuditEntries.OrderBy(a => a.OccurredAt).Select(a => a.Action).ToListAsync());
        }

        // The daily sequence continues after the restart.
        var next = await second.Commands.ReceiveAsync(
            new ReceiptRequest(DevelopmentData.Receiving.Value, [new QuantityLine(DevelopmentData.Gadget.Value, 1)]),
            new CommandContext(actor, Guid.NewGuid()),
            CancellationToken.None);
        Assert.EndsWith("-000002", next.Value.Response.TransactionNo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idempotent_replay_and_conflict_work_after_a_restart()
    {
        var key = Guid.NewGuid();
        var request = new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Gadget.Value, 5)]);
        InventoryTransactionResponse original;
        await using (var first = await _database.StartAsync())
        {
            original = (await first.Commands.ReceiveAsync(request, new CommandContext(DevelopmentData.ReceivingClerk, key), CancellationToken.None))
                .Value.Response;
        }

        await using var second = await _database.StartAsync();

        var replay = await second.Commands.ReceiveAsync(request, new CommandContext(DevelopmentData.ReceivingClerk, key), CancellationToken.None);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(original.TransactionNo, replay.Value.Response.TransactionNo);
        Assert.Equal(original.Balances.Select(b => b.AvailableQuantity), replay.Value.Response.Balances.Select(b => b.AvailableQuantity));

        var conflict = await second.Commands.ReceiveAsync(
            request with { Lines = [new QuantityLine(DevelopmentData.Gadget.Value, 6)] },
            new CommandContext(DevelopmentData.ReceivingClerk, key),
            CancellationToken.None);
        Assert.Equal(ProblemCodes.IdempotencyConflict, conflict.Error!.Code);

        // Keys belong to a user: another user's identical key is a new action.
        var otherUser = await second.Commands.ReceiveAsync(request, new CommandContext(DevelopmentData.Supervisor, key), CancellationToken.None);
        Assert.False(otherUser.Value.Replayed);

        Assert.Equal(10, await second.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Gadget));
        await using var db = second.CreateDb();
        Assert.Equal(2, await db.InventoryTransactions.CountAsync());
        Assert.Equal(2, await db.IdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task Expired_idempotency_key_is_replaced_and_executes_again()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));
        var key = Guid.NewGuid();
        var request = new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1)]);
        await using var host = await _database.StartAsync(clock: clock);

        var first = await host.Commands.ReceiveAsync(request, new CommandContext(DevelopmentData.ReceivingClerk, key), CancellationToken.None);
        clock.Advance(Application.Inventory.InventoryCommandService.IdempotencyRetention + TimeSpan.FromMinutes(1));
        var second = await host.Commands.ReceiveAsync(request, new CommandContext(DevelopmentData.ReceivingClerk, key), CancellationToken.None);

        Assert.False(second.Value.Replayed);
        Assert.NotEqual(first.Value.Response.TransactionNo, second.Value.Response.TransactionNo);
        Assert.Equal(2, await host.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
        await using var db = host.CreateDb();
        var record = Assert.Single(await db.IdempotencyRecords.ToListAsync());
        Assert.Equal(clock.GetUtcNow(), record.CreatedAt); // stored time round-trips exactly (UTC ticks)
    }

    [Fact]
    public async Task Refresh_token_rotation_and_reuse_detection_survive_a_restart()
    {
        LoginResponse login;
        await using (var first = await _database.StartAsync())
        {
            login = (await first.Auth.LoginAsync(new LoginRequest("shipper", TestHost.Password), CancellationToken.None)).Value;
        }

        await using var second = await _database.StartAsync();

        var rotated = await second.Auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None);
        Assert.True(rotated.IsSuccess);
        Assert.Equal(login.RefreshTokenExpiresAt, rotated.Value.RefreshTokenExpiresAt); // the session's absolute expiry is kept

        var reused = await second.Auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None);
        Assert.Equal(ProblemCodes.Unauthenticated, reused.Error!.Code);
        var afterTheft = await second.Auth.RefreshAsync(new RefreshRequest(rotated.Value.RefreshToken), CancellationToken.None);
        Assert.Equal(ProblemCodes.Unauthenticated, afterTheft.Error!.Code); // reuse ended the whole family

        await using var db = second.CreateDb();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.DoesNotContain(tokens, t => t.TokenHash == login.RefreshToken); // only hashes are stored
        Assert.Contains(tokens, t => t.TokenHash == AuthService.HashToken(login.RefreshToken));
        Assert.Equal(
            ["auth.login.succeeded", "auth.refresh.reused", "auth.refresh.reused"], // both reuses are audited
            await db.AuditEntries.OrderBy(a => a.OccurredAt).Select(a => a.Action).ToListAsync());
    }

    [Fact]
    public async Task Keyset_pages_of_balances_follow_location_then_product()
    {
        await using var host = await _database.StartAsync();
        foreach (var location in new[] { DevelopmentData.Receiving, DevelopmentData.ShelfA0101, DevelopmentData.ShelfA0102 })
        {
            Assert.True((await host.Commands.ReceiveAsync(
                new ReceiptRequest(location.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1), new QuantityLine(DevelopmentData.Gadget.Value, 1)]),
                new CommandContext(DevelopmentData.ReceivingClerk, Guid.NewGuid()),
                CancellationToken.None)).IsSuccess);
        }

        var seen = new List<StoredBalance>();
        string? after = null;
        do
        {
            var page = await host.Inventory.ListBalancesAsync(new BalanceFilter(), new PageRequest(after, 4), CancellationToken.None);
            seen.AddRange(page.Items);
            after = page.NextAfter;
        }
        while (after is not null);

        Assert.Equal(6, seen.Count);
        var keys = seen.Select(b => $"{b.Key.LocationId.Value:N}:{b.Key.ProductId.Value:N}").ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }
}
