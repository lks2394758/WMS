using System.Diagnostics;
using Wms.Contracts.Common;

namespace Wms.Api.Observability;

/// <summary>
/// Accepts a valid <see cref="ApiHeaders.CorrelationId"/> from the client or generates one, exposes it to the request
/// (<see cref="HttpContext.Items"/>, the current activity and a logging scope) and echoes it on every response.
/// Runs first so error, status-code and health responses carry the header too. The value is an opaque token: never
/// put user data in it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string ItemKey = "Wms.CorrelationId";
    private const string LogProperty = "CorrelationId";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
        var correlationId = IsValid(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        Activity.Current?.SetTag(LogProperty, correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { [LogProperty] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>The correlation ID of the current request, or <c>null</c> outside the middleware.</summary>
    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;

    /// <summary>1-64 characters from <c>[A-Za-z0-9._:-]</c> (see <see cref="ApiHeaders.CorrelationId"/>).</summary>
    internal static bool IsValid(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
}
