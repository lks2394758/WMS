using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wms.Api.Observability;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;

namespace Wms.Api.Tests;

/// <summary>
/// One structured completion entry per request (method, route template, status, duration) inside the correlation
/// scope, and nothing sensitive: no query string, request body, credential or token value in any captured log.
/// </summary>
public sealed class RequestLoggingTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public RequestLoggingTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentSeed:Password"] = _password }));
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Completed_request_is_logged_with_route_template_status_duration_and_correlation_id()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/barcodes/resolve?value=secret-query-value");
        request.Headers.Add(ApiHeaders.CorrelationId, "log-test-correlation-1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(_logs.Completed());
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("GET", entry.State["Method"]);
        Assert.Equal("api/v1/barcodes/resolve", entry.State["RouteTemplate"]);
        Assert.Equal(401, entry.State["StatusCode"]);
        Assert.True((double)entry.State["ElapsedMilliseconds"]! >= 0);
        Assert.Equal("log-test-correlation-1", entry.Scope["CorrelationId"]);
        _logs.AssertNothingContains("secret-query-value", "secret-token-value");
    }

    [Fact]
    public async Task Parameterised_and_unmatched_routes_log_the_template_not_the_path()
    {
        using var client = _factory.CreateClient();

        await client.GetAsync("/api/v1/products/7f1d3c1e-2b8a-4c5e-9a43-0d6c2f3b1a11");
        await client.GetAsync("/no-such-route/customer-name");
        await client.GetAsync("/health/live");

        Assert.Equal(
            ["api/v1/products/{id:guid}", RequestLoggingMiddleware.Unmatched, "/health/live"],
            _logs.Completed().Select(entry => entry.State["RouteTemplate"]));
        Assert.Equal([401, 404, 200], _logs.Completed().Select(entry => entry.State["StatusCode"]));
        // The framework's own request scope carries RequestPath (never the query string); our entry carries only the template.
        Assert.All(_logs.Completed(), entry => Assert.DoesNotContain(
            entry.State.Values.Append(entry.Message).OfType<string>(),
            value => value.Contains("7f1d3c1e", StringComparison.Ordinal) || value.Contains("customer-name", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Login_credentials_and_issued_tokens_are_never_logged()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest("receiver", _password), WmsJson.Options);
        var tokens = await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(_logs.Completed());
        Assert.Equal("POST", entry.State["Method"]);
        Assert.Equal("api/v1/auth/login", entry.State["RouteTemplate"]);
        Assert.Equal(200, entry.State["StatusCode"]);
        _logs.AssertNothingContains(_password, tokens!.AccessToken, tokens.RefreshToken);
    }

    private sealed record LogEntry(
        string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State, IReadOnlyDictionary<string, object?> Scope);

    /// <summary>Records every entry with its structured state and the active scope values.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public IEnumerable<LogEntry> Completed() =>
            _entries.Where(entry => entry.Category == typeof(RequestLoggingMiddleware).FullName);

        public void AssertNothingContains(params string[] secrets)
        {
            foreach (var entry in _entries)
            {
                var text = string.Join(
                    '\n',
                    new[] { entry.Message }
                        .Concat(entry.State.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))
                        .Concat(entry.Scope.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
                foreach (var secret in secrets)
                {
                    Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
                owner._scopes.ForEachScope(
                    (value, values) =>
                    {
                        if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                        {
                            foreach (var (key, item) in pairs)
                            {
                                values[key] = item;
                            }
                        }
                    },
                    scope);
                var structured = state is IEnumerable<KeyValuePair<string, object?>> properties
                    ? properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                owner._entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), structured, scope));
            }
        }
    }
}
