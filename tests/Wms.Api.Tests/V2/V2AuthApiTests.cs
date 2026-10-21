using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Api.Tests.V2;

/// <summary>
/// The v2 process mode (<c>V2:Enabled=true</c>) end to end on temporary files: schema and seed at start-up, real
/// login/me/switch-role/logout against persisted sessions, single-session eviction, expiry, restart, permission by
/// current role, logs without credentials, and the v1 database left byte-for-byte untouched.
/// </summary>
public sealed class V2AuthApiTests : IDisposable
{
    // Generated per run: no password is stored in the repository.
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-api-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero));
    private readonly byte[] _legacyBytes = RandomNumberGenerator.GetBytes(4096);

    public V2AuthApiTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        File.WriteAllBytes(LegacyPath, _legacyBytes); // stands in for an existing v1 wms-demo.db
    }

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private string LegacyPath => Path.Combine(_root, "App_Data", "wms-demo.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Login_me_and_logout_use_the_persisted_session()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        var loginResponse = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("Receiver ", _password), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var raw = await loginResponse.Content.ReadAsStringAsync();
        var login = JsonSerializer.Deserialize<LoginResponse>(raw, WmsJson.Options)!;

        Assert.Equal(V2SessionTokenLength, login.Token.Length);
        Assert.Equal(_clock.GetUtcNow().AddHours(8), login.ExpiresAt);
        Assert.Equal(RoleNames.ReceivingClerk, login.User.CurrentRole);
        Assert.Equal(new[] { PermissionNames.InventoryReceive }, login.User.Permissions);
        Assert.DoesNotContain(PermissionNames.InventoryMove, login.User.Permissions);
        Assert.DoesNotContain(PermissionNames.InventoryCount, login.User.Permissions);
        Assert.DoesNotContain(PermissionNames.InventoryAdjust, login.User.Permissions);
        Assert.True(WireId.IsValid(login.User.Id));
        Assert.DoesNotContain("PBKDF2", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Hash\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);

        Authorize(client, login.Token);
        var me = (await client.GetFromJsonAsync<SessionResponse>("/api/v2/auth/me", WmsJson.Options))!;
        Assert.Equal(login.User.Id, me.User.Id);
        Assert.Equal("receiver", me.User.Username);
        Assert.Equal(new[] { RoleNames.ReceivingClerk }, me.User.Roles);
        Assert.Equal(login.User.Permissions, me.User.Permissions);
        Assert.Equal(login.ExpiresAt, me.ExpiresAt);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        await AssertProblemAsync(await client.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);
        await AssertProblemAsync(await client.PostAsync("/api/v2/auth/logout", null), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);

        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM LoginSessions WHERE LoggedOutAt IS NOT NULL"));
        Assert.Equal(64L, Scalar<long>("SELECT length(TokenHash) FROM LoginSessions"));
        Assert.Equal(0L, Scalar<long>($"SELECT COUNT(*) FROM LoginSessions WHERE TokenHash = '{login.Token}'"));
    }

    [Fact]
    public async Task A_new_login_revokes_the_previous_session_atomically()
    {
        using var factory = CreateFactory();
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();

        Authorize(first, (await LoginAsync(first, "shipper")).Token);
        Authorize(second, (await LoginAsync(second, "SHIPPER")).Token);

        await AssertProblemAsync(await first.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v2/auth/me")).StatusCode);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM LoginSessions WHERE RevokedAt IS NOT NULL AND RevokeReason = 'replacedByNewLogin'"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL"));
    }

    [Fact]
    public async Task Switch_role_keeps_the_token_and_changes_permissions_to_the_current_role_only()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await client.GetAsync("/health/live");
        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\",\"receivingClerk\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");

        var login = await LoginAsync(client, "supervisor");
        Assert.Equal(RoleNames.WarehouseSupervisor, login.User.CurrentRole);
        Assert.Contains(PermissionNames.InventoryCount, login.User.Permissions);
        Authorize(client, login.Token);

        var switched = await client.PostAsJsonAsync("/api/v2/auth/switch-role", new SwitchRoleRequest(RoleNames.ReceivingClerk), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var session = (await switched.Content.ReadFromJsonAsync<SessionResponse>(WmsJson.Options))!;
        Assert.Equal(RoleNames.ReceivingClerk, session.User.CurrentRole);
        Assert.Equal(new[] { PermissionNames.InventoryReceive }, session.User.Permissions);
        Assert.Equal(login.ExpiresAt, session.ExpiresAt);

        var me = (await client.GetFromJsonAsync<SessionResponse>("/api/v2/auth/me", WmsJson.Options))!; // same token
        Assert.Equal(RoleNames.ReceivingClerk, me.User.CurrentRole);
        Assert.Equal("receivingClerk", Scalar<string>("SELECT CurrentRole FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL"));

        var denied = await client.PostAsJsonAsync("/api/v2/auth/switch-role", new SwitchRoleRequest(RoleNames.SystemAdministrator), WmsJson.Options);
        await AssertProblemAsync(denied, HttpStatusCode.Forbidden, ProblemCodesV2.RoleNotAssigned);
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'switchRole' AND Success = 1"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'switchRole' AND Success = 0"));

        // Removing the current role from the account ends the session at once.
        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        await AssertProblemAsync(await client.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);
    }

    [Fact]
    public async Task Login_can_choose_an_assigned_role_and_rejects_others()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var chosen = await client.PostAsJsonAsync(
            "/api/v2/auth/login", new LoginRequest("admin", _password, RoleNames.SystemAdministrator), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(PermissionNames.All, (await chosen.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!.User.Permissions);

        var other = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", _password, RoleNames.ShippingClerk), WmsJson.Options);
        await AssertProblemAsync(other, HttpStatusCode.Forbidden, ProblemCodesV2.RoleNotAssigned);
    }

    [Fact]
    public async Task Expired_sessions_and_disabled_accounts_are_rejected_on_the_next_request()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Authorize(client, (await LoginAsync(client, "receiver")).Token);

        _clock.Advance(TimeSpan.FromHours(8));
        await AssertProblemAsync(await client.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionExpired);

        Authorize(client, (await LoginAsync(client, "receiver")).Token);
        Execute("UPDATE Users SET Enabled = 0, Version = Version + 1 WHERE NormalizedUsername = 'RECEIVER'");
        await AssertProblemAsync(await client.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("receiver", _password), WmsJson.Options),
            HttpStatusCode.Unauthorized,
            ProblemCodesV2.InvalidCredentials);
    }

    [Fact]
    public async Task Failed_logins_are_logged_without_credentials()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var wrong = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", wrong), WmsJson.Options),
            HttpStatusCode.Unauthorized,
            ProblemCodesV2.InvalidCredentials);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("nobody", wrong), WmsJson.Options),
            HttpStatusCode.Unauthorized,
            ProblemCodesV2.InvalidCredentials);
        var token = (await LoginAsync(client, "admin")).Token;
        await AssertProblemAsync(await client.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.Unauthenticated);

        var logs = Scalar<string>("SELECT group_concat(coalesce(LoginName,'') || '|' || Description || '|' || coalesce(FailureReason,'') || '|' || Subjects, '\n') FROM ActivityLogs");
        Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'login' AND Success = 0 AND ActorId IS NULL"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'login' AND Success = 1 AND ActorRole = 'systemAdministrator'"));
        Assert.DoesNotContain(wrong, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(_password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("PBKDF2", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody", logs, StringComparison.Ordinal); // masked
    }

    [Fact]
    public async Task Sessions_seed_and_passwords_survive_a_restart_and_v1_is_untouched()
    {
        string token;
        string hash;
        using (var first = CreateFactory())
        {
            using var client = first.CreateClient();
            token = (await LoginAsync(client, "admin")).Token;
            hash = Scalar<string>("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'");
        }

        SqliteConnection.ClearAllPools();
        using var second = CreateFactory(seedPassword: Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));
        using var restarted = second.CreateClient();
        Authorize(restarted, token);

        Assert.Equal(HttpStatusCode.OK, (await restarted.GetAsync("/api/v2/auth/me")).StatusCode);
        Assert.Equal(hash, Scalar<string>("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'"));
        Assert.Equal(4L, Scalar<long>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM __EFMigrationsHistory"));

        // v1 is not served in v2 mode, and the v1 file was never opened.
        Assert.Equal(HttpStatusCode.NotFound, (await restarted.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await restarted.GetAsync("/openapi/v1.json")).StatusCode);
        SqliteConnection.ClearAllPools();
        Assert.Equal(_legacyBytes, await File.ReadAllBytesAsync(LegacyPath));
        Assert.False(File.Exists(LegacyPath + "-wal"));
        Assert.False(File.Exists(LegacyPath + "-shm"));
    }

    [Fact]
    public void Startup_refuses_a_v2_path_pointing_at_the_v1_database()
    {
        using var factory = CreateFactory(dataSource: LegacyPath);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("V2:Sqlite:DataSource", error.Message, StringComparison.Ordinal);
        Assert.Equal(_legacyBytes, File.ReadAllBytes(LegacyPath));
    }

    [Fact]
    public async Task Without_a_seed_password_no_account_exists_and_login_fails()
    {
        using var factory = CreateFactory(seedPassword: "");
        using var client = factory.CreateClient();

        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", _password), WmsJson.Options),
            HttpStatusCode.Unauthorized,
            ProblemCodesV2.InvalidCredentials);
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM Users"));
        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM SystemSettings"));
    }

    [Fact]
    public async Task Write_conflicts_answer_409_concurrency_conflict_not_500()
    {
        using var factory = CreateFactory(configure: services =>
        {
            // Wrap the real v2 store: log appends and role switches fail as if the writer lock timed out.
            var real = services.Single(d => d.ServiceType == typeof(IV2IdentityStore));
            services.Remove(real);
            services.AddSingleton<IV2IdentityStore>(sp => new ThrowingStore(
                (IV2IdentityStore)real.ImplementationFactory!(sp), () => new PersistenceConflictException()));
        });
        using var client = factory.CreateClient();

        // Failed login: its log append conflicts; the central handler maps it.
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", "wrong-password-1"), WmsJson.Options),
            HttpStatusCode.Conflict,
            ProblemCodesV2.ConcurrencyConflict);

        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\",\"receivingClerk\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        Authorize(client, (await LoginAsync(client, "supervisor")).Token);
        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/switch-role", new SwitchRoleRequest(RoleNames.ReceivingClerk), WmsJson.Options),
            HttpStatusCode.Conflict,
            ProblemCodesV2.ConcurrencyConflict);
        Assert.Equal("warehouseSupervisor", Scalar<string>("SELECT CurrentRole FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL"));
    }

    [Fact]
    public async Task Bodies_over_4_mib_are_rejected_with_413()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var body = new ByteArrayContent(new byte[(4 * 1024 * 1024) + 1]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        await AssertProblemAsync(await client.PostAsync("/api/v2/auth/login", body), HttpStatusCode.RequestEntityTooLarge, ProblemCodesV2.PayloadTooLarge);

        // B5b1: the framework rejection is audited once, right after the response (bounded wait), naming no account.
        for (var i = 0; i < 40 && Scalar<long>("SELECT COUNT(*) FROM ActivityLogs") == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM ActivityLogs"));
        Assert.Equal(1L, Scalar<long>(
            "SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'login' AND Success = 0 AND FailureCode = 'PAYLOAD_TOO_LARGE' " +
            "AND Description = '请求被拒绝' AND ActorId IS NULL AND ActorName IS NULL AND ActorRole IS NULL AND LoginName IS NULL " +
            "AND Subjects = '[]' AND Changes IS NULL"));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM LoginSessions"));
    }

    // The in-memory test server has no chunked-body limit, so Kestrel's exception is simulated where request processing
    // would surface it. The real chunked path is covered by the real-Kestrel e2e script.
    [Fact]
    public async Task Server_body_limit_exception_maps_to_413_payload_too_large()
    {
        using var factory = CreateFactory(configure: services => ThrowFromStore(
            services, () => new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)));
        using var client = factory.CreateClient();

        await AssertProblemAsync(
            await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", "wrong-password-1"), WmsJson.Options),
            HttpStatusCode.RequestEntityTooLarge,
            ProblemCodesV2.PayloadTooLarge);
    }

    [Fact]
    public async Task Other_bad_requests_and_malformed_json_are_not_mapped_to_413()
    {
        using var factory = CreateFactory(configure: services => ThrowFromStore(
            services, () => new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest)));
        using var client = factory.CreateClient();

        var other = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest("admin", "wrong-password-1"), WmsJson.Options);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, other.StatusCode);
        Assert.DoesNotContain(ProblemCodesV2.PayloadTooLarge, await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var malformed = new StringContent("{\"username\":", System.Text.Encoding.UTF8, "application/json");
        await AssertProblemAsync(await client.PostAsync("/api/v2/auth/login", malformed), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
    }

    /// <summary>Wraps the real v2 store so failed-login log appends and role switches throw <paramref name="error"/>.</summary>
    private static void ThrowFromStore(IServiceCollection services, Func<Exception> error)
    {
        var real = services.Single(d => d.ServiceType == typeof(IV2IdentityStore));
        services.Remove(real);
        services.AddSingleton<IV2IdentityStore>(sp => new ThrowingStore((IV2IdentityStore)real.ImplementationFactory!(sp), error));
    }

    private const int V2SessionTokenLength = 43;

    private WebApplicationFactory<Program> CreateFactory(
        string? seedPassword = null, string? dataSource = null, Action<IServiceCollection>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("V2:Enabled", "true")
            .UseSetting("V2:Sqlite:DataSource", dataSource ?? DatabasePath)
            .UseSetting("V2:Seed:Password", seedPassword ?? _password)
            .UseSetting("Persistence:Sqlite:DataSource", LegacyPath)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000)); // fast; real format
                configure?.Invoke(services);
            }));

    /// <summary>Delegates to the real store; log appends and role switches throw <paramref name="error"/>.</summary>
    private sealed class ThrowingStore(IV2IdentityStore inner, Func<Exception> error) : IV2IdentityStore
    {
        public Task<V2UserRecord?> FindUserByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken) =>
            inner.FindUserByNormalizedUsernameAsync(normalizedUsername, cancellationToken);

        public Task<int> GetSessionHoursAsync(CancellationToken cancellationToken) => inner.GetSessionHoursAsync(cancellationToken);

        public Task<V2SessionRecord?> StartSessionAsync(
            long userId, int expectedUserVersion, string role, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt,
            string revokeReason, V2ActivityEntry log, CancellationToken cancellationToken) =>
            inner.StartSessionAsync(userId, expectedUserVersion, role, tokenHash, now, expiresAt, revokeReason, log, cancellationToken);

        public Task<V2AuthenticatedSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            inner.FindSessionByTokenHashAsync(tokenHash, cancellationToken);

        public Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            throw error();

        public Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            inner.EndSessionAsync(sessionId, now, log, cancellationToken);

        public Task AppendActivityAsync(V2ActivityEntry entry, CancellationToken cancellationToken) =>
            throw error();
    }

    private async Task<LoginResponse> LoginAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(username, _password), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(WmsJson.Options);
        Assert.Equal(code, problem!.Code);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
