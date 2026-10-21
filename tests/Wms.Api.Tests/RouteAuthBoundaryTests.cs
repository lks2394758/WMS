using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Identity;

namespace Wms.Api.Tests;

/// <summary>
/// The HTTP boundary between routing and authorization: an unknown route is 404 for everyone, while every known
/// endpoint still needs a signed-in user (401) and every write still needs its permission (403). All three are
/// <c>application/problem+json</c> with the stable code and a trace ID.
/// </summary>
public sealed class RouteAuthBoundaryTests : IDisposable
{
    // Generated per run: no password is stored in the repository.
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly WebApplicationFactory<Program> _factory;

    public RouteAuthBoundaryTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentSeed:Password"] = _password })));
    }

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("GET", "/does-not-exist")]
    [InlineData("GET", "/api/v1/no-such-resource")]
    [InlineData("GET", "/api/v1/products/not-a-guid")]
    [InlineData("POST", "/api/v1/no-such-resource")]
    [InlineData("GET", "/api/v1/no-such-file.json")]
    public async Task Anonymous_request_to_an_unknown_route_is_not_found(string method, string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, ProblemCodes.NotFound);
    }

    [Theory]
    [InlineData("/api/v1/products")]
    [InlineData("/api/v1/products/7f1d3c1e-2b8a-4c5e-9a43-0d6c2f3b1a11")]
    [InlineData("/api/v1/locations")]
    [InlineData("/api/v1/barcodes/resolve?value=4006381333931")]
    public async Task Catalog_endpoint_without_a_token_is_unauthenticated(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ProblemCodes.Unauthenticated);
    }

    [Fact]
    public async Task Catalog_endpoint_with_an_invalid_token_is_unauthenticated()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/v1/products");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ProblemCodes.Unauthenticated);
    }

    [Theory]
    [InlineData(UserRole.ShippingClerk, "/api/v1/inventory/receipts")]
    [InlineData(UserRole.ShippingClerk, "/api/v1/inventory/movements")]
    [InlineData(UserRole.ReceivingClerk, "/api/v1/inventory/shipments")]
    [InlineData(UserRole.ReceivingClerk, "/api/v1/inventory/counts")]
    [InlineData(UserRole.ReceivingClerk, "/api/v1/inventory/adjustments")]
    public async Task Signed_in_role_without_the_write_permission_is_forbidden(UserRole role, string path)
    {
        using var client = await SignInAsync(role);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(
                new ReceiptRequest(DevelopmentData.ShelfA0101.Value, [new QuantityLine(DevelopmentData.Widget.Value, 1)]),
                options: WmsJson.Options),
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ProblemCodes.Forbidden);
    }

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

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<WmsProblemDetails>(WmsJson.Options);
        Assert.Equal(code, problem!.Code);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
    }
}
