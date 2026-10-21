using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Wms.Api.Errors;
using Wms.Api.Health;
using Wms.Api.Observability;
using Wms.Api.RateLimiting;
using Wms.Application;
using Wms.Application.V2;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;
using Wms.Persistence.Sqlite;
using Wms.Persistence.Sqlite.V2;

namespace Wms.Api.V2;

/// <summary>
/// The API v2 process mode, opted in with <c>V2:Enabled=true</c> (e.g. <c>V2__Enabled=true</c>). It serves only
/// <c>/api/v2</c>, health and nothing of v1: no JWT, no v1 controllers, no OpenAPI v1, and it never opens, migrates,
/// seeds or health-reads the v1 file. Its database is <c>V2:Sqlite:DataSource</c> (default
/// <c>App_Data/wms-v2-demo.db</c>); a path equal to or named like the v1 file is refused at startup.
/// </summary>
internal static class V2Startup
{
    public const string EnabledKey = "V2:Enabled";
    public const string SeedEnabledKey = "V2:Seed:Enabled";
    public const string SeedPasswordKey = "V2:Seed:Password";
    private const string LegacyDataSourceKey = "Persistence:Sqlite:DataSource";
    private const string ApiPolicy = "api-v2";
    private const string ReadinessCheck = "persistence-v2";

    /// <summary>Request body limit of the v2 contract (contracts/v2/README.md §1): larger bodies get 413 PAYLOAD_TOO_LARGE.</summary>
    public const long MaxRequestBodyBytes = 4 * 1024 * 1024;

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue(EnabledKey, false);

    public static async Task RunAsync(WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var extensions = context.ProblemDetails.Extensions;
            extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            extensions.TryAdd("code", V2Problems.CodeOf(context.ProblemDetails.Status));
        });
        builder.Services.AddExceptionHandler<V2ExceptionHandler>(); // write conflicts → 409; server body limit → 413; else 500
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes); // also chunked bodies
        builder.Services.ConfigureHttpJsonOptions(options => WmsJson.Apply(options.SerializerOptions));
        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new V2ControllerFeatureProvider()))
            .AddJsonOptions(options => WmsJson.Apply(options.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
            {
                V2RejectionAuditMiddleware.Mark(context.HttpContext, StatusCodes.Status400BadRequest, ProblemCodesV2.ValidationFailed);
                return ValidationProblems.FromModelState(context);
            });
        builder.Services.AddHealthChecks()
            .AddCheck<V2PersistenceReadinessCheck>(ReadinessCheck, HealthStatus.Unhealthy, [ReadinessChecks.ReadyTag]);

        builder.Services.AddAuthentication(V2SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, V2SessionAuthenticationHandler>(V2SessionAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder(V2SessionAuthenticationHandler.SchemeName).RequireAuthenticatedUser().Build();
            foreach (var permission in PermissionNames.All)
            {
                options.AddPolicy(permission, policy => policy
                    .AddAuthenticationSchemes(V2SessionAuthenticationHandler.SchemeName)
                    .RequireClaim(V2Claims.Permission, permission));
            }
        });
        AddRateLimiting(builder);

        builder.Services.AddWmsV2Application();
        var storage = builder.Configuration.GetSection(V2SqliteOptions.Section).Get<V2SqliteOptions>() ?? new V2SqliteOptions();
        storage.DataSource = V2SqliteOptions.ResolveDataSource(
            storage.DataSource,
            builder.Environment.ContentRootPath,
            builder.Configuration[LegacyDataSourceKey] ?? SqliteOptions.DefaultDataSource);
        builder.Services.AddWmsV2SqlitePersistence(storage);

        var app = builder.Build();
        await InitializeAsync(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<V2RejectionAuditMiddleware>(); // outside every framework rejection site, so it sees the final status
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(LimitRequestBodyAsync);
        app.UseHttpsRedirection();
        app.Use((context, next) =>
        {
            if (context.GetEndpoint() is not null)
            {
                return next(context);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = ReadinessChecks.IsReadinessCheck }).AllowAnonymous();
        app.MapControllers().RequireRateLimiting(ApiPolicy);

        await app.RunAsync();
    }

    /// <summary>
    /// Rejects a declared body over <see cref="MaxRequestBodyBytes"/> before anything reads it, and lowers the server's
    /// own limit for undeclared (chunked) bodies where the server supports it (Kestrel; the in-memory test server has none).
    /// </summary>
    private static async Task LimitRequestBodyAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            V2RejectionAuditMiddleware.Mark(context, StatusCodes.Status413PayloadTooLarge, ProblemCodesV2.PayloadTooLarge);
            await V2Problems.WriteAsync(
                context,
                new DomainError(ProblemCodesV2.PayloadTooLarge, $"The request body exceeds {MaxRequestBodyBytes} bytes."));
            return;
        }

        await next(context);
    }

    private static async Task InitializeAsync(WebApplication app)
    {
        var report = await V2SqlitePersistence.InitializeAsync(app.Services);
        app.Logger.LogInformation(
            "API v2 mode. v2 SQLite database {DataSource}: file created {FileCreated}, migrations applied {AppliedMigrations}. The v1 database is not opened.",
            report.DataSource,
            report.FileCreated,
            report.AppliedMigrations.Count == 0 ? "none (up to date)" : string.Join(", ", report.AppliedMigrations));

        if (!app.Configuration.GetValue(SeedEnabledKey, true))
        {
            app.Logger.LogInformation("{Key} is false; the v2 database was not seeded.", SeedEnabledKey);
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<V2Seeder>()
            .SeedAsync(app.Configuration[SeedPasswordKey], CancellationToken.None);
        app.Logger.LogInformation(
            "v2 seed: {UsersInserted} accounts inserted, preset unit inserted {UnitInserted}, settings inserted {SettingsInserted}. Existing rows are never changed.",
            outcome.Report.UsersInserted,
            outcome.Report.UnitInserted,
            outcome.Report.SettingsInserted);
        if (outcome.AccountsWaitingForPassword.Count > 0)
        {
            app.Logger.LogWarning(
                "{Key} is {State}; the missing v2 accounts {Accounts} were NOT created. Set an 8-256 character password with " +
                "dotnet user-secrets or the environment variable V2__Seed__Password and restart.",
                SeedPasswordKey,
                outcome.PasswordRejected ? "too short or blank" : "not configured",
                string.Join(", ", outcome.AccountsWaitingForPassword));
        }
    }

    private static void AddRateLimiting(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<RateLimitSettings>()
            .Bind(builder.Configuration.GetSection(RateLimitSettings.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (rejected, _) =>
            {
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    rejected.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                V2RejectionAuditMiddleware.Mark(rejected.HttpContext, StatusCodes.Status429TooManyRequests, ProblemCodesV2.RateLimited);
                await V2Problems.WriteAsync(rejected.HttpContext, new DomainError(ProblemCodesV2.RateLimited, "Too many requests."));
            };
            options.AddPolicy(ApiPolicy, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;
                var user = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst(V2Claims.Subject)?.Value : null;
                var key = user is not null ? "user:" + user : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });
    }
}

/// <summary>In v2 mode, the only controllers are the internal ones in <c>Wms.Api.V2</c>; every v1 controller is removed.</summary>
internal sealed class V2ControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    public static IReadOnlyList<TypeInfo> Controllers { get; } =
    [
        typeof(V2AuthController).GetTypeInfo(),
        typeof(V2UnitsController).GetTypeInfo(),
        typeof(V2CategoriesController).GetTypeInfo(),
        typeof(V2ProductsController).GetTypeInfo(),
        typeof(V2BinsController).GetTypeInfo(),
        typeof(V2UsersController).GetTypeInfo(),
        typeof(V2SettingsController).GetTypeInfo(),
        typeof(V2InventoryController).GetTypeInfo(),
        typeof(V2SerializedItemsController).GetTypeInfo(),
        typeof(V2CountsController).GetTypeInfo(),
        typeof(V2AlertsController).GetTypeInfo(),
        typeof(V2ActivityLogsController).GetTypeInfo(),
        typeof(V2DashboardController).GetTypeInfo(),
    ];

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        feature.Controllers.Clear();
        foreach (var controller in Controllers)
        {
            feature.Controllers.Add(controller);
        }
    }
}

/// <summary>
/// Central mapping of the known v2 exceptions; everything else stays unhandled (500 <c>INTERNAL_ERROR</c>):
/// <list type="bullet">
/// <item><see cref="PersistenceConflictException"/> (lock timeout, optimistic version or unique-index race in any v2
/// write not mapped by its use case) → 409 <c>CONCURRENCY_CONFLICT</c>. Nothing was committed; retry.</item>
/// <item>The server's own request-body limit (Kestrel throws <see cref="BadHttpRequestException"/> with
/// <c>StatusCode</c> 413 while a chunked body is being read, e.g. by the JSON input formatter) → 413
/// <c>PAYLOAD_TOO_LARGE</c>. Only that exact status is mapped; other bad requests and JSON errors are untouched.</item>
/// </list>
/// </summary>
internal sealed class V2ExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is PersistenceConflictException)
        {
            await V2Problems.WriteAsync(
                httpContext,
                new DomainError(ProblemCodesV2.ConcurrencyConflict, "The v2 database was busy or changed concurrently; nothing was committed. Retry."));
            return true;
        }

        if (IsBodyTooLarge(exception))
        {
            V2RejectionAuditMiddleware.Mark(httpContext, StatusCodes.Status413PayloadTooLarge, ProblemCodesV2.PayloadTooLarge);
            await V2Problems.WriteAsync(
                httpContext,
                new DomainError(ProblemCodesV2.PayloadTooLarge, $"The request body exceeds {V2Startup.MaxRequestBodyBytes} bytes."));
            return true;
        }

        return false;
    }

    /// <summary>The body-limit exception itself or wrapped (formatters may wrap read failures).</summary>
    internal static bool IsBodyTooLarge(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads the v2 settings row through the v2 port; never touches the v1 file.</summary>
internal sealed class V2PersistenceReadinessCheck(IV2HealthProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await probe.CanReadAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The v2 settings row is missing.");
}
