using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application;
using Wms.Application.Auth;
using Wms.Application.V2;
using Wms.Application.V2.Auth;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;
using Wms.Persistence.Sqlite.V2;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>
/// The account snapshot the password was verified against is re-checked inside the session write transaction: a
/// password reset, disable or role change committed in between opens no session. Deterministic: the "concurrent" change
/// is committed exactly between the read and the write.
/// </summary>
public sealed class V2IdentityStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-identity-tests", Guid.NewGuid().ToString("N"));
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 1_000);
    private ServiceProvider _services = null!;

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private IV2IdentityStore Store => _services.GetRequiredService<IV2IdentityStore>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPasswordHasher>(_hasher);
        services.AddWmsV2Application();
        services.AddWmsV2SqlitePersistence(new V2SqliteOptions { DataSource = DatabasePath });
        _services = services.BuildServiceProvider();
        await V2SqlitePersistence.InitializeAsync(_services);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<V2Seeder>().SeedAsync(_password, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("UPDATE Users SET PasswordHash = '{0}', Version = Version + 1 WHERE NormalizedUsername = 'ADMIN'")] // password reset
    [InlineData("UPDATE Users SET Enabled = 0, Version = Version + 1 WHERE NormalizedUsername = 'ADMIN'")] // disabled
    [InlineData("UPDATE Users SET DisplayName = 'Changed', Version = Version + 1 WHERE NormalizedUsername = 'ADMIN'")] // any edit
    public async Task Start_session_with_a_stale_account_snapshot_writes_nothing(string change)
    {
        var snapshot = (await Store.FindUserByNormalizedUsernameAsync("ADMIN", CancellationToken.None))!;
        Assert.True(_hasher.Verify(_password, snapshot.PasswordHash)); // the old password was verified ...

        Execute(string.Format(System.Globalization.CultureInfo.InvariantCulture, change, _hasher.Hash("another-password-1")));

        var session = await Store.StartSessionAsync( // ... and only then does the session write run
            snapshot.Id,
            snapshot.Version,
            RoleNames.SystemAdministrator,
            V2SessionTokens.Hash(V2SessionTokens.Create()),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(8),
            V2AuthService.RevokedByNewLogin,
            new V2ActivityEntry(DateTimeOffset.UtcNow, "login", "登录", true, [], snapshot.Id, snapshot.DisplayName, RoleNames.SystemAdministrator),
            CancellationToken.None);

        Assert.Null(session);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM LoginSessions"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ActivityLogs"));
    }

    [Fact]
    public async Task Stale_snapshot_does_not_revoke_the_existing_session()
    {
        var auth = new V2AuthService(Store, _hasher, TimeProvider.System);
        var first = await auth.LoginAsync(new LoginRequest("admin", _password), CancellationToken.None);
        Assert.True(first.IsSuccess);
        var snapshot = (await Store.FindUserByNormalizedUsernameAsync("ADMIN", CancellationToken.None))!;

        Execute($"UPDATE Users SET PasswordHash = '{_hasher.Hash("another-password-1")}', Version = Version + 1 WHERE Id = {snapshot.Id}");
        var session = await Store.StartSessionAsync(
            snapshot.Id, snapshot.Version, RoleNames.SystemAdministrator, V2SessionTokens.Hash(V2SessionTokens.Create()),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(8), V2AuthService.RevokedByNewLogin,
            new V2ActivityEntry(DateTimeOffset.UtcNow, "login", "登录", true, []), CancellationToken.None);

        Assert.Null(session);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL"));
    }

    [Fact]
    public async Task Login_racing_a_password_reset_gets_no_token_and_no_session()
    {
        var resetHash = _hasher.Hash("another-password-1");
        var racing = new ResetBeforeSessionStore(Store, () => Execute(
            $"UPDATE Users SET PasswordHash = '{resetHash}', Version = Version + 1 WHERE NormalizedUsername = 'ADMIN'"));
        var auth = new V2AuthService(racing, _hasher, TimeProvider.System);

        var result = await auth.LoginAsync(new LoginRequest("admin", _password), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ProblemCodesV2.ConcurrencyConflict, result.Error!.Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM LoginSessions"));

        // A retry verifies against the new hash: the old password no longer opens anything.
        var retry = await new V2AuthService(Store, _hasher, TimeProvider.System)
            .LoginAsync(new LoginRequest("admin", _password), CancellationToken.None);
        Assert.Equal(ProblemCodesV2.InvalidCredentials, retry.Error!.Code);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM LoginSessions"));
    }

    [Fact]
    public async Task Switch_role_rechecks_the_account_inside_the_transaction()
    {
        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\",\"receivingClerk\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        var auth = new V2AuthService(Store, _hasher, TimeProvider.System);
        var login = (await auth.LoginAsync(new LoginRequest("supervisor", _password), CancellationToken.None)).Value;
        var principal = (await auth.AuthenticateAsync(login.Token, CancellationToken.None)).Principal!;

        // The role is removed after the request was authenticated with it.
        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\"]', Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        var removed = await auth.SwitchRoleAsync(principal, new SwitchRoleRequest(RoleNames.ReceivingClerk), CancellationToken.None);
        Assert.Equal(ProblemCodesV2.RoleNotAssigned, removed.Error!.Code);

        Execute("UPDATE Users SET Roles = '[\"warehouseSupervisor\",\"receivingClerk\"]', Enabled = 0, Version = Version + 1 WHERE NormalizedUsername = 'SUPERVISOR'");
        var disabled = await auth.SwitchRoleAsync(principal, new SwitchRoleRequest(RoleNames.ReceivingClerk), CancellationToken.None);
        Assert.Equal(ProblemCodesV2.SessionRevoked, disabled.Error!.Code);

        Assert.Equal("warehouseSupervisor", ScalarText("SELECT CurrentRole FROM LoginSessions"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'switchRole' AND Success = 1"));
    }

    [Fact]
    public async Task Write_conflicts_of_switch_role_and_logout_become_concurrency_conflict()
    {
        var auth = new V2AuthService(Store, _hasher, TimeProvider.System);
        var login = (await auth.LoginAsync(new LoginRequest("admin", _password), CancellationToken.None)).Value;
        var principal = (await auth.AuthenticateAsync(login.Token, CancellationToken.None)).Principal!;
        var conflicting = new V2AuthService(new ConflictingStore(Store), _hasher, TimeProvider.System);

        var switched = await conflicting.SwitchRoleAsync(
            principal with { Roles = [RoleNames.SystemAdministrator, RoleNames.ShippingClerk] },
            new SwitchRoleRequest(RoleNames.ShippingClerk),
            CancellationToken.None);
        var logout = await conflicting.LogoutAsync(principal, CancellationToken.None);

        Assert.Equal(ProblemCodesV2.ConcurrencyConflict, switched.Error!.Code);
        Assert.Equal(ProblemCodesV2.ConcurrencyConflict, logout.Error!.Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL"));
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql) => Convert.ToInt64(Query(sql), System.Globalization.CultureInfo.InvariantCulture);

    private string ScalarText(string sql) => Convert.ToString(Query(sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private object Query(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    /// <summary>Commits <paramref name="concurrentChange"/> after the password check and before the session write.</summary>
    private sealed class ResetBeforeSessionStore(IV2IdentityStore inner, Action concurrentChange) : DelegatingStore(inner)
    {
        public override Task<V2SessionRecord?> StartSessionAsync(
            long userId, int expectedUserVersion, string role, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt,
            string revokeReason, V2ActivityEntry log, CancellationToken cancellationToken)
        {
            concurrentChange();
            return base.StartSessionAsync(userId, expectedUserVersion, role, tokenHash, now, expiresAt, revokeReason, log, cancellationToken);
        }
    }

    /// <summary>Every session write fails as if the writer lock timed out.</summary>
    private sealed class ConflictingStore(IV2IdentityStore inner) : DelegatingStore(inner)
    {
        public override Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            throw new PersistenceConflictException();

        public override Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            throw new PersistenceConflictException();
    }

    private class DelegatingStore(IV2IdentityStore inner) : IV2IdentityStore
    {
        public Task<V2UserRecord?> FindUserByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken) =>
            inner.FindUserByNormalizedUsernameAsync(normalizedUsername, cancellationToken);

        public Task<int> GetSessionHoursAsync(CancellationToken cancellationToken) => inner.GetSessionHoursAsync(cancellationToken);

        public virtual Task<V2SessionRecord?> StartSessionAsync(
            long userId, int expectedUserVersion, string role, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt,
            string revokeReason, V2ActivityEntry log, CancellationToken cancellationToken) =>
            inner.StartSessionAsync(userId, expectedUserVersion, role, tokenHash, now, expiresAt, revokeReason, log, cancellationToken);

        public Task<V2AuthenticatedSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            inner.FindSessionByTokenHashAsync(tokenHash, cancellationToken);

        public virtual Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            inner.SwitchRoleAsync(sessionId, role, now, log, cancellationToken);

        public virtual Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
            inner.EndSessionAsync(sessionId, now, log, cancellationToken);

        public Task AppendActivityAsync(V2ActivityEntry entry, CancellationToken cancellationToken) =>
            inner.AppendActivityAsync(entry, cancellationToken);
    }
}
