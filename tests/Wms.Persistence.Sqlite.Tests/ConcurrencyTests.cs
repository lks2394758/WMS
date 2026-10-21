using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Inventory;

namespace Wms.Persistence.Sqlite.Tests;

/// <summary>
/// Concurrent writers on one SQLite file. Separate hosts have separate in-process queues, so they meet at SQLite's
/// file lock, as separate processes would. This is evidence for the SQLite demonstration provider only; it proves
/// nothing about SQL Server or PostgreSQL locking (AGENTS.md rule 4).
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Concurrent_shipments_from_several_hosts_never_oversell()
    {
        const int stock = 12;
        const int attempts = 30;
        var hosts = await StartHostsAsync(3);
        try
        {
            await ReceiveAsync(hosts[0], stock, Guid.NewGuid());

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Enumerable.Range(0, attempts).Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await hosts[i % hosts.Length].Commands.ShipAsync(
                    new ShipmentRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1)]),
                    new CommandContext(DevelopmentData.ShippingClerk, Guid.NewGuid()),
                    CancellationToken.None);
            })).ToList();
            start.SetResult(); // release every writer at once
            var results = await Task.WhenAll(pending);

            Assert.Equal(stock, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal(ProblemCodes.InsufficientStock, r.Error!.Code));
            Assert.Equal(0, await hosts[1].AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));

            await using var db = hosts[2].CreateDb();
            var shipments = await db.InventoryTransactions
                .Where(t => t.Type == (int)InventoryTransactionType.Shipment)
                .Select(t => t.Number)
                .ToListAsync();
            Assert.Equal(stock, shipments.Count);
            Assert.Equal(
                Enumerable.Range(1, stock).Select(n => n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)),
                shipments.Select(n => n[^6..]).Order(StringComparer.Ordinal)); // gap-free, no duplicates
            var balance = await db.Balances.SingleAsync();
            Assert.Equal(1 + stock, balance.Version); // one receipt and one write per successful shipment
        }
        finally
        {
            await DisposeAllAsync(hosts);
        }
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_commit_once()
    {
        const int attempts = 10;
        var hosts = await StartHostsAsync(2);
        try
        {
            var key = Guid.NewGuid();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Enumerable.Range(0, attempts).Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await ReceiveAsync(hosts[i % hosts.Length], 4, key);
            })).ToList();
            start.SetResult(); // release every writer at once
            var results = await Task.WhenAll(pending);

            Assert.All(results, r => Assert.True(r.IsSuccess));
            Assert.Single(results, r => !r.Value.Replayed);
            Assert.Single(results.Select(r => r.Value.Response.TransactionNo).Distinct());
            Assert.Equal(4, await hosts[0].AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
            await using var db = hosts[1].CreateDb();
            Assert.Equal(1, await db.InventoryTransactions.CountAsync());
            Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
        }
        finally
        {
            await DisposeAllAsync(hosts);
        }
    }

    [Fact]
    public async Task Database_lock_timeout_is_a_safe_conflict_and_the_same_key_succeeds_afterwards()
    {
        await using var holder = await _database.StartAsync();
        await using var waiter = await _database.StartAsync(lockTimeoutSeconds: 1);
        var key = Guid.NewGuid();

        var stopwatch = Stopwatch.StartNew();
        await using (await holder.Store.BeginWriteAsync(CancellationToken.None)) // BEGIN IMMEDIATE: holds the file's writer lock
        {
            var rejected = await ReceiveAsync(waiter, 5, key);

            Assert.Equal(ProblemCodes.ConcurrencyConflict, rejected.Error!.Code);
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(0.9), $"rejected after {stopwatch.Elapsed}");

            // Readers are not blocked by the open write (WAL).
            Assert.Equal(0, await waiter.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
        }

        await using (var db = waiter.CreateDb())
        {
            Assert.Equal(0, await db.InventoryTransactions.CountAsync());
            Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
        }

        var retried = await ReceiveAsync(waiter, 5, key);
        Assert.True(retried.IsSuccess);
        Assert.False(retried.Value.Replayed); // the rejected attempt stored nothing, so this is the first execution
        Assert.Equal(5, await waiter.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
    }

    [Fact]
    public async Task In_process_queue_timeout_is_a_safe_conflict()
    {
        await using var host = await _database.StartAsync(lockTimeoutSeconds: 1);
        var key = Guid.NewGuid();

        await using (await host.Store.BeginWriteAsync(CancellationToken.None))
        {
            var rejected = await ReceiveAsync(host, 2, key);
            Assert.Equal(ProblemCodes.ConcurrencyConflict, rejected.Error!.Code);
        }

        Assert.True((await ReceiveAsync(host, 2, key)).IsSuccess);
        Assert.Equal(2, await host.AvailableAsync(DevelopmentData.ShelfA0101, DevelopmentData.Widget));
    }

    [Fact]
    public async Task Concurrent_refresh_of_one_token_rotates_it_once()
    {
        var hosts = await StartHostsAsync(2);
        try
        {
            var login = (await hosts[0].Auth.LoginAsync(new LoginRequest("receiver", TestHost.Password), CancellationToken.None)).Value;

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = hosts.Select(host => Task.Run(async () =>
            {
                await start.Task;
                return await host.Auth.RefreshAsync(new RefreshRequest(login.RefreshToken), CancellationToken.None);
            })).ToList();
            start.SetResult(); // release every writer at once
            var results = await Task.WhenAll(pending);

            Assert.Single(results, r => r.IsSuccess);
            await using var db = hosts[0].CreateDb();
            Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.ReplacedById != null)); // exactly one rotation
            Assert.Equal(2, await db.RefreshTokens.CountAsync());
        }
        finally
        {
            await DisposeAllAsync(hosts);
        }
    }

    private async Task<TestHost[]> StartHostsAsync(int count)
    {
        var hosts = new TestHost[count];
        for (var i = 0; i < count; i++)
        {
            hosts[i] = await _database.StartAsync();
        }

        return hosts;
    }

    private static async Task DisposeAllAsync(IEnumerable<TestHost> hosts)
    {
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }
    }

    private static Task<Domain.Common.Result<InventoryCommandOutcome>> ReceiveAsync(TestHost host, int quantity, Guid key) =>
        host.Commands.ReceiveAsync(
            new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, quantity)]),
            new CommandContext(DevelopmentData.ReceivingClerk, key),
            CancellationToken.None);
}
