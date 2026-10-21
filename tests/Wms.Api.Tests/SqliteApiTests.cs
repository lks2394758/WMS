using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;

namespace Wms.Api.Tests;

/// <summary>
/// The full HTTP pipeline on the SQLite demonstration provider with an on-disk file: startup creates the directory,
/// migrates and seeds; a second host on the same file sees the committed balance, replays the stored idempotent
/// response and accepts the refresh token issued by the first host. SQLite evidence only (AGENTS.md rule 4).
/// </summary>
public sealed class SqliteApiTests : IDisposable
{
    // Generated per run: no password or signing key is stored in the repository.
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-api-sqlite-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_root, "nested", "App_Data", "wms-demo.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Committed_data_idempotent_replay_and_refresh_survive_a_host_restart()
    {
        var key = Guid.NewGuid();
        var receipt = new ReceiptRequest(DevelopmentData.ShelfA0102.Value, [new QuantityLine(DevelopmentData.Gadget.Value, 7)]);
        string transactionNo;
        string refreshToken;

        using (var first = CreateFactory())
        {
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            var login = await SignInAsync(client, "receiver");
            refreshToken = login.RefreshToken;

            var response = await PostWriteAsync(client, "/api/v1/inventory/receipts", receipt, key);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            transactionNo = (await response.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!.TransactionNo;
        }

        SqliteConnection.ClearAllPools(); // the first host is gone: nothing of it stays open
        Assert.True(File.Exists(DatabasePath));

        using var second = CreateFactory();
        using var restarted = second.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await restarted.GetAsync("/health/ready")).StatusCode);

        // The refresh token issued before the restart rotates; presenting it again ends the session.
        var refreshed = await restarted.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var session = (await refreshed.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
        var reused = await restarted.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);

        restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var balances = await restarted.GetFromJsonAsync<CursorPage<InventoryBalanceResponse>>(
            $"/api/v1/inventory/balances?productId={DevelopmentData.Gadget.Value}&locationId={DevelopmentData.ShelfA0102.Value}",
            WmsJson.Options);
        Assert.Equal(7, Assert.Single(balances!.Items).AvailableQuantity);

        var replay = await PostWriteAsync(restarted, "/api/v1/inventory/receipts", receipt, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues(ApiHeaders.IdempotentReplayed)));
        Assert.Equal(transactionNo, (await replay.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!.TransactionNo);

        var conflict = await PostWriteAsync(
            restarted, "/api/v1/inventory/receipts", receipt with { Lines = [new QuantityLine(DevelopmentData.Gadget.Value, 8)] }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(ProblemCodes.IdempotencyConflict, (await conflict.Content.ReadFromJsonAsync<WmsProblemDetails>(WmsJson.Options))!.Code);

        var after = await restarted.GetFromJsonAsync<CursorPage<InventoryBalanceResponse>>(
            $"/api/v1/inventory/balances?productId={DevelopmentData.Gadget.Value}&locationId={DevelopmentData.ShelfA0102.Value}",
            WmsJson.Options);
        Assert.Equal(7, Assert.Single(after!.Items).AvailableQuantity); // replay and conflict changed nothing
    }

    [Fact]
    public void Unknown_provider_fails_at_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Persistence:Provider", "Oracle"));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Persistence:Provider", error.Message, StringComparison.Ordinal);
    }

    // UseSetting, not ConfigureAppConfiguration: the provider is chosen while services are registered, before the
    // host is built, and only host settings are visible to Program at that point.
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Persistence:Provider", "Sqlite")
            .UseSetting("Persistence:Sqlite:DataSource", DatabasePath)
            .UseSetting("DevelopmentSeed:Password", _password)
            .UseSetting("Jwt:SigningKey", _signingKey));

    private async Task<LoginResponse> SignInAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return login;
    }

    private static Task<HttpResponseMessage> PostWriteAsync<T>(HttpClient client, string path, T body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key.ToString());
        return client.SendAsync(request);
    }
}
