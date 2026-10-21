using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Wms.Contracts.Common;

namespace Wms.Api.Tests;

public sealed class CorrelationIdTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Supplied_correlation_id_is_echoed_on_the_response()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(ApiHeaders.CorrelationId, "scan-gun-07:abc.123_x");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("scan-gun-07:abc.123_x", Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has space")]
    [InlineData("<script>")]
    [InlineData("x123456789x123456789x123456789x123456789x123456789x123456789x1234")] // 65 characters
    public async Task Missing_or_invalid_correlation_id_is_replaced_by_a_generated_one(string? supplied)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        if (supplied is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiHeaders.CorrelationId, supplied);
        }

        using var response = await client.SendAsync(request);

        var generated = Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId));
        Assert.Matches("^[0-9a-f]{32}$", generated);
    }

    [Theory]
    [InlineData("/no-such-route", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/products", HttpStatusCode.Unauthorized)]
    public async Task Error_responses_carry_the_correlation_id(string path, HttpStatusCode status)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ApiHeaders.CorrelationId, "error-path-1");

        using var response = await client.SendAsync(request);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("error-path-1", Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId)));
    }
}
