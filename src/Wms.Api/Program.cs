using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Wms.Api.Auth;
using Wms.Api.Errors;
using Wms.Api.Health;
using Wms.Api.Observability;
using Wms.Api.OpenApi;
using Wms.Api.Persistence;
using Wms.Api.RateLimiting;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Contracts.Common;

var builder = WebApplication.CreateBuilder(args);

// API v2 is a separate, opt-in process mode (V2:Enabled=true) with its own database file; it serves no v1 endpoint
// and never opens the v1 database. Without the setting, everything below runs exactly as before.
if (Wms.Api.V2.V2Startup.IsEnabled(builder.Configuration))
{
    await Wms.Api.V2.V2Startup.RunAsync(builder);
    return;
}

// Errors: application/problem+json with a stable code and the trace ID (guide §7.5).
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        var extensions = context.ProblemDetails.Extensions;
        extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        extensions.TryAdd("code", ProblemCodeMapper.FromStatus(context.ProblemDetails.Status));
    };
});
builder.Services.ConfigureHttpJsonOptions(options => WmsJson.Apply(options.SerializerOptions));
builder.Services.AddControllers()
    .AddJsonOptions(options => WmsJson.Apply(options.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = ValidationProblems.FromModelState);
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<BearerSecurityTransformers>();
    options.AddOperationTransformer<BearerSecurityTransformers>();
    options.AddDocumentTransformer<WireContractTransformers>();
    options.AddOperationTransformer<WireContractTransformers>();
    options.AddSchemaTransformer<WireContractTransformers>();
});
builder.Services.AddHealthChecks().AddWmsReadinessChecks(); // persistence + jwt-signing, run by /health/ready only

// Authentication: HS256 JWT bearer; the key is resolved once by JwtSigning (never stored in appsettings).
builder.Services.AddOptions<JwtSettings>().Bind(builder.Configuration.GetSection(JwtSettings.Section));
builder.Services.AddSingleton<JwtSigning>();
builder.Services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtSigning>((options, signing) => JwtSigning.ConfigureBearer(options, signing));
builder.Services.AddAuthorization(AuthorizationSetup.Configure);

// Rate limiting: built-in fixed window per user or client address on the /api controllers only (RateLimitSetup).
builder.Services.AddWmsRateLimiting(builder.Configuration);

// Use cases and persistence. Persistence:Provider selects the provider (PersistenceSetup): Sqlite, the
// demonstration database file (default), or InMemory, for tests and Development only. Neither is the production
// database of the M4 gate.
builder.Services.AddWmsApplication();
var persistenceProvider = builder.AddWmsPersistence();

var app = builder.Build();

// Fail at startup, not on the first request, if the signing configuration is invalid.
_ = app.Services.GetRequiredService<JwtSigning>();

// Schema migrations (SQLite) and the idempotent development seed, before the first request. The shared seed
// password comes from user-secrets or the environment, never from a committed file.
await app.InitializeWmsPersistenceAsync(persistenceProvider);

app.UseMiddleware<CorrelationIdMiddleware>(); // first, so every response (errors and health included) echoes it
app.UseMiddleware<RequestLoggingMiddleware>(); // inside the correlation scope; sees the final status of every request
app.UseExceptionHandler();
app.UseStatusCodePages(); // empty 401/403/404 responses become problem details
app.UseHttpsRedirection();

// No endpoint matched (routing runs first, implicitly): answer 404 here, because the fallback policy would otherwise
// turn an unknown route into 401. Every matched endpoint still goes through authorization below.
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
app.UseRateLimiter(); // after authentication (partition by user), before authorization (anonymous floods are limited too)
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous(); // GET /openapi/v1.json
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous(); // process is up; runs no checks
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = ReadinessChecks.IsReadinessCheck })
    .AllowAnonymous(); // runs the "ready" checks (ReadinessChecks): 200 Healthy or 503 Unhealthy
app.MapControllers().RequireRateLimiting(RateLimitSetup.ApiPolicy); // every controller endpoint requires a signed-in user unless [AllowAnonymous]; rate limited

app.Run();

/// <summary>Exposed for WebApplicationFactory-based API tests.</summary>
public partial class Program;
