using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wms.Domain.Catalog;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Api.Tests;

/// <summary>
/// <c>/health/ready</c> runs the named readiness checks (persistence, jwt-signing); <c>/health/live</c> runs none.
/// A broken dependency makes readiness 503 without leaking the failure detail, while liveness stays 200.
/// </summary>
public sealed class ReadinessTests : IDisposable
{
    private const string FailureDetail = "simulated-provider-outage";

    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Readiness_runs_the_named_checks_and_they_are_healthy()
    {
        var report = await _factory.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(["jwt-signing", "persistence"], report.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.All(report.Entries.Values, entry => Assert.Contains("ready", entry.Tags));

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unavailable_persistence_makes_readiness_fail_but_not_liveness()
    {
        using var failing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogReader>();
            services.AddSingleton<ICatalogReader, UnavailableCatalogReader>();
        }));
        using var client = failing.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain(FailureDetail, body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var report = await failing.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["persistence"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["jwt-signing"].Status);
    }

    /// <summary>A provider whose store cannot be reached: every call throws.</summary>
    private sealed class UnavailableCatalogReader : ICatalogReader
    {
        public Task<Product?> FindProductAsync(ProductId id, CancellationToken cancellationToken) => throw Outage();

        public Task<PageResult<Product>> ListProductsAsync(PageRequest page, CancellationToken cancellationToken) => throw Outage();

        public Task<Location?> FindLocationAsync(LocationId id, CancellationToken cancellationToken) => throw Outage();

        public Task<PageResult<Location>> ListLocationsAsync(PageRequest page, CancellationToken cancellationToken) => throw Outage();

        public Task<IReadOnlyDictionary<ProductId, IReadOnlyList<string>>> GetActiveBarcodesAsync(
            IReadOnlyCollection<ProductId> productIds, CancellationToken cancellationToken) => throw Outage();

        public Task<IReadOnlyList<Barcode>> FindActiveBarcodesAsync(string value, CancellationToken cancellationToken) =>
            throw Outage();

        private static InvalidOperationException Outage() => new(FailureDetail);
    }
}
