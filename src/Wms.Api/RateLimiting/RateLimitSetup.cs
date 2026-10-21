using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Wms.Api.Auth;
using Wms.Api.Errors;
using Wms.Contracts.Common;

namespace Wms.Api.RateLimiting;

/// <summary>Configuration section <c>RateLimiting</c>: one fixed window per partition for the <c>/api</c> controllers.</summary>
public sealed class RateLimitSettings
{
    public const string Section = "RateLimiting";

    /// <summary>Requests allowed per partition in one window.</summary>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 120;

    [Range(1, 3_600)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Built-in ASP.NET Core rate limiting. The named <see cref="ApiPolicy"/> is attached to the controller endpoints only,
/// so health checks and the OpenAPI document are never limited. A request is counted against its signed-in user
/// (validated token only) or, failing that, its client address; excess requests get 429 <c>RATE_LIMITED</c> at once
/// (no queueing). Limits are per process: they protect the server, they are not a quota.
/// </summary>
internal static class RateLimitSetup
{
    public const string ApiPolicy = "api";

    public static IServiceCollection AddWmsRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitSettings>()
            .Bind(configuration.GetSection(RateLimitSettings.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (rejected, _) => WriteProblemAsync(rejected);
            options.AddPolicy(ApiPolicy, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });
    }

    /// <summary>
    /// The user ID from a validated token, else the connection's remote address. Behind a reverse proxy every client
    /// shares the proxy's address until forwarded headers are configured for the trusted proxy (deployment, M6).
    /// </summary>
    internal static string PartitionKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindUserId() is { } userId)
        {
            return "user:" + userId.Value.ToString("D");
        }

        return context.Connection.RemoteIpAddress is { } address ? "ip:" + address : "ip:unknown";
    }

    private static async ValueTask WriteProblemAsync(OnRejectedContext rejected)
    {
        var httpContext = rejected.HttpContext;
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = ProblemCatalog.TitleOf(ProblemCodes.RateLimited),
            Type = ProblemCatalog.TypeOf(ProblemCodes.RateLimited),
            Detail = "Too many requests. Retry after the interval in the Retry-After header.",
        };
        problem.Extensions["code"] = ProblemCodes.RateLimited;

        // IProblemDetailsService runs CustomizeProblemDetails (traceId) and writes application/problem+json.
        var writer = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await writer.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
