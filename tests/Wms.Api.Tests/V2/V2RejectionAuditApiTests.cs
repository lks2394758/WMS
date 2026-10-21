using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Api.Tests.V2;

/// <summary>B5b1 framework-level audit of rejected v2 mutations over HTTP on temporary files (in-memory test server).</summary>
public sealed class V2RejectionAuditApiTests : IDisposable
{
    private const string Secret = "S3CRET-marker-7f1c";
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-reject-audit-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Each_framework_rejection_of_a_mutation_is_logged_once_without_secrets()
    {
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();
        var (admin, adminToken) = await SignedInAsync(factory, "admin");
        using var receiver = (await SignedInAsync(factory, "receiver")).Client;
        using var adminClient = admin;

        // 401 without a session (actor never named).
        var unauth = await anonymous.PostAsJsonAsync($"/api/v2/units?token={Secret}", new UnitWriteRequest(Secret), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        await unauth.Content.ReadAsStringAsync();

        // 403 for the current role (actor = the session validated in this request).
        var forbidden = await receiver.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest(Secret), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await forbidden.Content.ReadAsStringAsync();

        // 400 malformed JSON, 415 unsupported content type.
        var malformed = await admin.PostAsync("/api/v2/units", new StringContent("{\"name\": \"" + Secret, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await malformed.Content.ReadAsStringAsync();
        var plain = await admin.PostAsync("/api/v2/units", new StringContent(Secret, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        await plain.Content.ReadAsStringAsync();

        // 413 declared body over the limit (rejected before authentication: no actor), valid route ID as subject.
        var big = new ByteArrayContent(new byte[(4 * 1024 * 1024) + 1]);
        big.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var tooLarge = await admin.PutAsync("/api/v2/products/123", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        await tooLarge.Content.ReadAsStringAsync();

        // Revoked credentials: the old token of a replaced session is 401 and never names the account.
        using var fresh = (await SignedInAsync(factory, "admin")).Client;
        var revoked = await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest(Secret), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await revoked.Content.ReadAsStringAsync();

        // Reads are never audited.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v2/units")).StatusCode);

        var rows = await RowsAsync("SELECT FailureCode, IFNULL(ActorRole, '-'), Subjects FROM ActivityLogs WHERE Description = '请求被拒绝' ORDER BY Id", 6);
        Assert.Equal(
            new[]
            {
                ("UNAUTHENTICATED", "-", "[]"),
                ("FORBIDDEN", "receivingClerk", "[]"),
                ("VALIDATION_FAILED", "systemAdministrator", "[]"),
                ("VALIDATION_FAILED", "systemAdministrator", "[]"),
                ("PAYLOAD_TOO_LARGE", "-", "[{\"type\":\"product\",\"id\":\"123\",\"label\":null}]"),
                ("SESSION_REVOKED", "-", "[]"),
            },
            rows.Select(r => (r[0], r[1], r[2])));
        Assert.Equal(5L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Description = '请求被拒绝' AND Type = 'unitCreate' AND Success = 0"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Description = '请求被拒绝' AND Type = 'productUpdate'"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Description = '请求被拒绝' AND ActorId IS NULL AND ActorName IS NULL"));

        // No body, query, token or credential text anywhere in the log; entries are immutable.
        var everything = string.Join('\n', (await RowsAsync("SELECT Description || IFNULL(FailureReason,'') || Subjects || IFNULL(Changes,'') || IFNULL(LoginName,'') FROM ActivityLogs", 0)).Select(r => r[0]));
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(adminToken, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_password, everything, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => Execute("UPDATE ActivityLogs SET FailureReason = 'x' WHERE Description = '请求被拒绝'"));
    }

    [Fact]
    public async Task Use_case_and_login_failures_are_not_logged_twice()
    {
        using var factory = CreateFactory();
        using var admin = (await SignedInAsync(factory, "admin")).Client;
        using var anonymous = factory.CreateClient();

        var invalid = await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest(" "), WmsJson.Options); // service validation
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await invalid.Content.ReadAsStringAsync();
        var badLogin = await anonymous.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", "wrong-" + Secret), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        await badLogin.Content.ReadAsStringAsync();
        var badBodyLogin = await anonymous.PostAsync("/api/v2/auth/login", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badBodyLogin.StatusCode);
        await badBodyLogin.Content.ReadAsStringAsync();

        await RowsAsync("SELECT Id FROM ActivityLogs WHERE Description = '请求被拒绝'", 1);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitCreate' AND Success = 0"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitCreate' AND Description = '请求被拒绝'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'login' AND Success = 0 AND FailureCode = 'INVALID_CREDENTIALS'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'login' AND Description = '请求被拒绝' AND FailureCode = 'VALIDATION_FAILED' AND ActorId IS NULL"));
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM ActivityLogs WHERE IFNULL(LoginName,'') LIKE '%{Secret}%' OR IFNULL(FailureReason,'') LIKE '%{Secret}%'"));
    }

    [Fact]
    public async Task Rate_limited_mutations_are_logged_once_each_with_the_trusted_actor()
    {
        using var factory = CreateFactory(permitLimit: 3);
        using var admin = (await SignedInAsync(factory, "admin")).Client;
        var limited = 0;
        for (var i = 0; i < 6; i++)
        {
            var response = await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest($"Unit {i}"), WmsJson.Options);
            await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.Contains("Retry-After"));
                limited++;
            }
        }

        Assert.True(limited > 0);
        await RowsAsync("SELECT Id FROM ActivityLogs WHERE FailureCode = 'RATE_LIMITED'", limited);
        Assert.Equal((long)limited, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'unitCreate' AND FailureCode = 'RATE_LIMITED' AND ActorRole = 'systemAdministrator'"));
    }

    [Fact]
    public async Task A_failing_audit_store_keeps_the_original_rejection_and_never_gets_the_request_token()
    {
        var store = new FailingAuditStore();
        using var baseFactory = CreateFactory(permitLimit: 2); // login and the anonymous post share the IP partition
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            var original = services.Last(d => d.ServiceType == typeof(IV2IdentityStore));
            services.AddSingleton<IV2IdentityStore>(sp =>
            {
                store.Inner = (IV2IdentityStore)original.ImplementationFactory!(sp);
                store.Accessor = sp.GetRequiredService<IHttpContextAccessor>();
                return store;
            });
        }));
        using var anonymous = factory.CreateClient();
        using var admin = (await SignedInAsync(factory, "admin")).Client;

        var unauth = await anonymous.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("x"), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Equal("application/problem+json", unauth.Content.Headers.ContentType?.MediaType);
        Assert.Contains("UNAUTHENTICATED", await unauth.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        (await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("First"), WmsJson.Options)).Dispose();
        (await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("Second"), WmsJson.Options)).Dispose();
        var limited = await admin.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("Third"), WmsJson.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Contains("RATE_LIMITED", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        for (var i = 0; i < 40 && store.Attempts < 2; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, store.Attempts);
        Assert.Empty(store.Problems); // every append got its own cancellable token, never the request's
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Description = '请求被拒绝'"));
    }

    /// <summary>Delegates to the real store but fails every framework-rejection audit append, recording the token it got.</summary>
    private sealed class FailingAuditStore : IV2IdentityStore
    {
        private int _attempts;

        public IV2IdentityStore Inner { get; set; } = null!;

        public IHttpContextAccessor Accessor { get; set; } = null!;

        public System.Collections.Concurrent.ConcurrentQueue<string> Problems { get; } = new();

        public int Attempts => Volatile.Read(ref _attempts);

        public Task AppendActivityAsync(V2ActivityEntry entry, CancellationToken cancellationToken)
        {
            if (entry.Description != "请求被拒绝")
            {
                return Inner.AppendActivityAsync(entry, cancellationToken);
            }

            if (!cancellationToken.CanBeCanceled)
            {
                Problems.Enqueue("token cannot be cancelled (unbounded)");
            }

            if (Accessor.HttpContext is { } http && cancellationToken == http.RequestAborted)
            {
                Problems.Enqueue("request token reused");
            }

            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("simulated storage failure");
        }

        public Task<V2UserRecord?> FindUserByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken) =>
            Inner.FindUserByNormalizedUsernameAsync(normalizedUsername, cancellationToken);

        public Task<int> GetSessionHoursAsync(CancellationToken cancellationToken) => Inner.GetSessionHoursAsync(cancellationToken);

        public Task<V2SessionRecord?> StartSessionAsync(
            long userId, int expectedUserVersion, string role, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt, string revokeReason,
            V2ActivityEntry log, CancellationToken cancellationToken) =>
            Inner.StartSessionAsync(userId, expectedUserVersion, role, tokenHash, now, expiresAt, revokeReason, log, cancellationToken);

        public Task<V2AuthenticatedSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Inner.FindSessionByTokenHashAsync(tokenHash, cancellationToken);

        public Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            Inner.SwitchRoleAsync(sessionId, role, now, log, cancellationToken);

        public Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            Inner.EndSessionAsync(sessionId, now, log, cancellationToken);
    }

    /// <summary>Reads rows; the audit is written right after the response, so wait briefly for <paramref name="atLeast"/> rows.</summary>
    private async Task<List<string[]>> RowsAsync(string sql, int atLeast)
    {
        for (var attempt = 0; ; attempt++)
        {
            var rows = new List<string[]>();
            using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray());
                }
            }

            if (rows.Count >= atLeast || attempt >= 40)
            {
                return rows;
            }

            await Task.Delay(50);
        }
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private WebApplicationFactory<Program> CreateFactory(int? permitLimit = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder
                .UseSetting("V2:Enabled", "true")
                .UseSetting("V2:Sqlite:DataSource", DatabasePath)
                .UseSetting("V2:Seed:Password", _password)
                .UseSetting("Persistence:Sqlite:DataSource", Path.Combine(_root, "App_Data", "wms-demo.db"))
                .ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000)));
            if (permitLimit is { } limit)
            {
                builder.UseSetting("RateLimiting:PermitLimit", limit.ToString(CultureInfo.InvariantCulture));
            }
        });

    private async Task<(HttpClient Client, string Token)> SignedInAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(username, _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return (client, login.Token);
    }
}
