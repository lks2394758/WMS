using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Catalog;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Identity;

namespace Wms.Api.Tests;

/// <summary>
/// End-to-end through the HTTP pipeline against the in-memory provider: authentication, the permission policies,
/// idempotent writes and the problem contract. Each test gets its own host, so no state leaks between tests.
/// This proves wiring and behaviour, not database locking (AGENTS.md rule 4).
/// </summary>
public sealed class ApiSmokeTests : IDisposable
{
    // Generated per run: no password is stored in the repository.
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly WebApplicationFactory<Program> _factory;

    public ApiSmokeTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentSeed:Password"] = _password })));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Liveness_and_openapi_are_anonymous()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Readiness_is_anonymous_and_healthy()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_on_the_response()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(ApiHeaders.CorrelationId, "smoke-test-correlation-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("smoke-test-correlation-1", Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId)));
    }

    [Fact]
    public async Task Endpoints_require_a_signed_in_user()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ProblemCodes.Unauthenticated);
    }

    [Fact]
    public async Task Unknown_route_is_a_not_found_problem()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/not-a-real-route");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ProblemCodes.NotFound);
    }

    [Fact]
    public async Task Wrong_password_is_unauthenticated()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest("receiver", _password + "x"), WmsJson.Options);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ProblemCodes.Unauthenticated);
    }

    [Fact]
    public async Task Login_returns_tokens_and_me_returns_role_and_permissions()
    {
        using var client = await SignInAsync(UserRole.ReceivingClerk);

        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/v1/auth/me", WmsJson.Options);

        Assert.Equal("receiver", me!.Username);
        Assert.Equal(Roles.ReceivingClerk, me.Role);
        Assert.Equal(Permissions.InventoryReceive, Assert.Single(me.Permissions));
    }

    [Fact]
    public async Task Catalog_reads_return_seeded_data()
    {
        using var client = await SignInAsync(UserRole.ShippingClerk);

        var products = await client.GetFromJsonAsync<CursorPage<ProductResponse>>("/api/v1/products", WmsJson.Options);
        var resolved = await client.GetFromJsonAsync<BarcodeResolutionResponse>(
            "/api/v1/barcodes/resolve?value=4006381333931", WmsJson.Options);

        Assert.Contains(products!.Items, p => p.Id == DevelopmentData.Widget.Value);
        Assert.Equal(DevelopmentData.Widget.Value, resolved!.Product.Id);
    }

    [Fact]
    public async Task Receipt_is_idempotent_and_updates_the_committed_balance()
    {
        using var client = await SignInAsync(UserRole.ReceivingClerk);
        var key = Guid.NewGuid();
        var request = new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 5)]);

        var first = await PostWriteAsync(client, "/api/v1/inventory/receipts", request, key);
        var replay = await PostWriteAsync(client, "/api/v1/inventory/receipts", request, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False(first.Headers.Contains(ApiHeaders.IdempotentReplayed));
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues(ApiHeaders.IdempotentReplayed)));

        var original = await first.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options);
        var replayed = await replay.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options);
        Assert.Equal(original!.TransactionNo, replayed!.TransactionNo);
        Assert.Equal(TransactionTypes.Receipt, original.Type);

        var balances = await client.GetFromJsonAsync<CursorPage<InventoryBalanceResponse>>(
            $"/api/v1/inventory/balances?productId={DevelopmentData.Widget.Value}", WmsJson.Options);
        Assert.Equal(5, Assert.Single(balances!.Items).AvailableQuantity);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_a_conflict()
    {
        using var client = await SignInAsync(UserRole.ReceivingClerk);
        var key = Guid.NewGuid();

        await PostWriteAsync(client, "/api/v1/inventory/receipts", Receipt(5), key);
        var response = await PostWriteAsync(client, "/api/v1/inventory/receipts", Receipt(6), key);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ProblemCodes.IdempotencyConflict);
    }

    [Fact]
    public async Task Write_without_idempotency_key_is_rejected()
    {
        using var client = await SignInAsync(UserRole.ReceivingClerk);

        var response = await client.PostAsJsonAsync("/api/v1/inventory/receipts", Receipt(1), WmsJson.Options);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.ValidationFailed);
    }

    [Fact]
    public async Task Receipt_with_a_non_positive_quantity_is_a_field_validation_problem()
    {
        using var client = await SignInAsync(UserRole.ReceivingClerk);

        var response = await PostWriteAsync(client, "/api/v1/inventory/receipts", Receipt(0), Guid.NewGuid());

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, ProblemCodes.ValidationFailed);
        Assert.NotNull(problem.Errors);
        Assert.NotEmpty(Assert.Contains("lines[0].quantity", problem.Errors));
    }

    [Fact]
    public async Task Role_without_the_permission_is_forbidden()
    {
        using var client = await SignInAsync(UserRole.ShippingClerk);

        var response = await PostWriteAsync(client, "/api/v1/inventory/receipts", Receipt(1), Guid.NewGuid());

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ProblemCodes.Forbidden);
    }

    [Fact]
    public async Task Shipping_more_than_available_is_rejected_and_changes_nothing()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);
        await PostWriteAsync(client, "/api/v1/inventory/receipts", Receipt(2), Guid.NewGuid());

        var response = await PostWriteAsync(
            client,
            "/api/v1/inventory/shipments",
            new ShipmentRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 3)]),
            Guid.NewGuid());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, ProblemCodes.InsufficientStock);
        var balances = await client.GetFromJsonAsync<CursorPage<InventoryBalanceResponse>>(
            $"/api/v1/inventory/balances?productId={DevelopmentData.Widget.Value}", WmsJson.Options);
        Assert.Equal(2, Assert.Single(balances!.Items).AvailableQuantity);
    }

    private static ReceiptRequest Receipt(int quantity) =>
        new(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, quantity)]);

    private async Task<HttpClient> SignInAsync(UserRole role)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(DevelopmentData.Usernames[role], _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> PostWriteAsync<T>(HttpClient client, string path, T body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key.ToString());
        return client.SendAsync(request);
    }

    /// <summary>Asserts the problem contract and returns the body (the response stream can be read only once).</summary>
    private static async Task<WmsProblemDetails> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<WmsProblemDetails>(WmsJson.Options);
        Assert.Equal(code, problem!.Code);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
        return problem;
    }
}
