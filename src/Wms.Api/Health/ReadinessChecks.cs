using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wms.Api.Auth;
using Wms.Application.Catalog;

namespace Wms.Api.Health;

/// <summary>
/// The checks behind <c>GET /health/ready</c>. Each one is tagged <see cref="ReadyTag"/>; <c>/health/live</c> runs none.
/// A failing check makes readiness 503 <c>Unhealthy</c>; the response never carries exception details.
/// </summary>
internal static class ReadinessChecks
{
    public const string ReadyTag = "ready";
    public const string Persistence = "persistence";
    public const string JwtSigning = "jwt-signing";

    public static IHealthChecksBuilder AddWmsReadinessChecks(this IHealthChecksBuilder builder) => builder
        .AddCheck<PersistenceReadinessCheck>(Persistence, HealthStatus.Unhealthy, [ReadyTag])
        .AddCheck<JwtSigningReadinessCheck>(JwtSigning, HealthStatus.Unhealthy, [ReadyTag]);

    public static bool IsReadinessCheck(HealthCheckRegistration registration) => registration.Tags.Contains(ReadyTag);
}

/// <summary>
/// Reads one location through the catalog use case, so it exercises whichever persistence provider is registered
/// (the in-memory provider until M4). A thrown exception is reported as unhealthy by the health check service.
/// </summary>
internal sealed class PersistenceReadinessCheck(CatalogQueries catalog) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var locations = await catalog.ListLocationsAsync(cursor: null, limit: 1, cancellationToken);
        return locations.IsFailure
            ? HealthCheckResult.Unhealthy("The catalog read failed: " + locations.Error!.Code)
            : HealthCheckResult.Healthy();
    }
}

/// <summary>Signs and verifies a probe with the configured key: access tokens can be issued and validated.</summary>
internal sealed class JwtSigningReadinessCheck(JwtSigning signing) : IHealthCheck
{
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("wms-readiness-probe");

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var algorithm = signing.Credentials.Algorithm;
        var factory = signing.Key.CryptoProviderFactory;
        var signer = factory.CreateForSigning(signing.Key, algorithm);
        try
        {
            var verifier = factory.CreateForVerifying(signing.Key, algorithm);
            try
            {
                return Task.FromResult(verifier.Verify(Probe, signer.Sign(Probe))
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("The signing key cannot verify its own signature."));
            }
            finally
            {
                factory.ReleaseSignatureProvider(verifier);
            }
        }
        finally
        {
            factory.ReleaseSignatureProvider(signer);
        }
    }
}
