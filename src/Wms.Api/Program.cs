using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Wms.Api.Errors;
using Wms.Contracts.Common;

var builder = WebApplication.CreateBuilder(args);

// Milestone M1 skeleton (guide §4, §7.1 steps 1, 2, 6, 7, 8). Authentication, controllers, SignalR and
// application/persistence registration are added in M2 in the order given by guide §7.1.
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
builder.Services.AddOpenApi("v1");
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpsRedirection();

app.MapOpenApi(); // GET /openapi/v1.json
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.Run();

/// <summary>Exposed for WebApplicationFactory-based API tests.</summary>
public partial class Program;
