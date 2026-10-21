using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Domain.Identity;

namespace Wms.Api.Tests;

/// <summary>
/// The <c>/api</c> controllers share one fixed window per signed-in user or client address. A tiny limit and a long
/// window make the test deterministic: no timing, no loops. Health checks and the OpenAPI document are never limited.
/// </summary>
public sealed class RateLimitTests : IDisposable
{
    private const int PermitLimit = 2;

    // Generated per run: no password is stored in the repository.
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly WebApplicationFactory<Program> _factory;

    public RateLimitTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DevelopmentSeed:Password"] = _password,
                    ["RateLimiting:PermitLimit"] = PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["RateLimiting:WindowSeconds"] = "3600",
                })));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Request_over_the_limit_is_rate_limited_and_health_and_openapi_are_not()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/products")).StatusCode);

        var limited = await client.GetAsync("/api/v1/products");

        await AssertRateLimitedAsync(limited);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Signed_in_user_has_a_window_of_its_own()
    {
        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync( // anonymous window: 1 of 2
            "/api/v1/auth/login",
            new LoginRequest(DevelopmentData.Usernames[UserRole.WarehouseSupervisor], _password),
            WmsJson.Options);
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options);
        using var signedIn = _factory.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/me")).StatusCode);
        await AssertRateLimitedAsync(await signedIn.GetAsync("/api/v1/auth/me"));

        // The user's requests did not use up the anonymous window.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/products")).StatusCode);
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        var problem = await response.Content.ReadFromJsonAsync<WmsProblemDetails>(WmsJson.Options);
        Assert.Equal(ProblemCodes.RateLimited, problem!.Code);
        Assert.Equal(429, problem.Status);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
    }
}
