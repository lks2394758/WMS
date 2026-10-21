using System.Diagnostics;

namespace Wms.Api.Observability;

/// <summary>
/// Logs one structured entry when a request completes: method, endpoint route template, status code and duration.
/// Runs inside <see cref="CorrelationIdMiddleware"/>, so the entry carries the <c>CorrelationId</c> scope. Only the
/// route template is logged (<c>api/v1/products/{id:guid}</c>), never the raw path, query string, headers, request
/// body, credentials or token values; a request that matched no endpoint is logged as <see cref="Unmatched"/>.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string Unmatched = "(unmatched)";

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var status = StatusCodes.Status500InternalServerError; // if an exception escapes the pipeline
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            LogCompleted(
                logger,
                context.Request.Method,
                RouteTemplate(context),
                status,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    internal static string RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } template ? template : Unmatched;

    [LoggerMessage(
        EventId = 1,
        EventName = "RequestCompleted",
        Level = LogLevel.Information,
        Message = "HTTP {Method} {RouteTemplate} responded {StatusCode} in {ElapsedMilliseconds:0.0} ms")]
    private static partial void LogCompleted(
        ILogger logger, string method, string routeTemplate, int statusCode, double elapsedMilliseconds);
}
