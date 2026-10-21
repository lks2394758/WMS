using Wms.Application.Auth;
using Wms.Application.V2.Admin;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.Tests.V2;

/// <summary>B2b account administration through the real services on a temporary v2 file.</summary>
public sealed class V2UserServiceTests : V2ServiceTestBase
{
    private const string NewPassword = "correct horse battery staple 1";

    private V2UserService Users => Get<V2UserService>();

    [Fact]
    public async Task Only_a_current_administrator_role_manages_accounts()
    {
        var supervisor = await SignInAsync("supervisor");
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Users.CreateAsync(supervisor, User("kim"), default)).Code);

        // Holding the administrator role is not enough: the session's current role counts, not the union.
        Execute("UPDATE Users SET Roles = '[\"receivingClerk\",\"systemAdministrator\"]', Version = Version + 1 WHERE NormalizedUsername = 'RECEIVER'");
        var receiver = await SignInAsync("receiver");
        Assert.Equal(RoleNames.ReceivingClerk, receiver.CurrentRole);
        Assert.Equal(ProblemCodesV2.Forbidden, Fail(await Users.CreateAsync(receiver, User("kim"), default)).Code);
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM Users"));
    }

    [Fact]
    public async Task Accounts_are_validated_unique_unicode_case_insensitively_and_never_expose_passwords()
    {
        var admin = await SignInAsync("admin");
        var created = Ok(await Users.CreateAsync(admin, User("Ärger", RoleNames.ShippingClerk, RoleNames.ReceivingClerk), default));
        Assert.Equal(new[] { RoleNames.ShippingClerk, RoleNames.ReceivingClerk }, created.Roles);
        Assert.False(created.HasActiveSession);

        Assert.Equal(ProblemCodesV2.AlreadyExists, Fail(await Users.CreateAsync(admin, User("ärger"), default)).Code);
        UserCreateRequest[] invalid =
        [
            User("has space"), User(new string('u', 65)), User("ok") with { Roles = [] }, User("ok") with { Roles = ["shippingClerk", "shippingClerk"] },
            User("ok") with { Roles = ["root"] }, User("ok") with { Password = "short" }, User("ok") with { DisplayName = " " },
        ];
        foreach (var request in invalid)
        {
            Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Users.CreateAsync(admin, request, default)).Code);
        }

        var logs = Text("SELECT group_concat(Description || '|' || coalesce(FailureReason, '') || '|' || coalesce(Changes, ''), '\n') FROM ActivityLogs");
        Assert.DoesNotContain(NewPassword, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("short", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("PBKDF2", logs, StringComparison.Ordinal);
        Assert.Equal(7L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'userCreate' AND FailureCode = 'VALIDATION_FAILED' AND ActorRole = 'systemAdministrator'"));
        Assert.DoesNotContain(typeof(UserResponse).GetProperties(), p => p.Name.Contains("Password", StringComparison.Ordinal) || p.Name.Contains("Hash", StringComparison.Ordinal));

        // The new account signs in with its own password; HasActiveSession follows real open sessions.
        var (token, _) = await LoginAsync("ÄRGER", NewPassword);
        Assert.True(Ok(await Users.GetAsync(created.Id, default)).HasActiveSession);
        Clock.Advance(TimeSpan.FromHours(9));
        Assert.False(Ok(await Users.GetAsync(created.Id, default)).HasActiveSession); // expired is not active
        Assert.Equal(ProblemCodesV2.SessionExpired, (await AuthenticateAsync(token)).Error!.Code);
    }

    [Fact]
    public async Task Role_changes_and_disabling_revoke_sessions_in_the_same_transaction_but_renames_do_not()
    {
        var admin = await SignInAsync("admin");
        var user = Ok(await Users.CreateAsync(admin, User("kim", RoleNames.ShippingClerk), default));
        var (token, _) = await LoginAsync("kim", NewPassword);

        var renamed = Ok(await Users.UpdateAsync(admin, user.Id, Update(user, displayName: "Kim Lee"), default));
        var me = (await AuthenticateAsync(token)).Principal!;
        Assert.Equal("Kim Lee", me.DisplayName); // same session, current name

        var regrouped = Ok(await Users.UpdateAsync(admin, user.Id, Update(renamed, roles: [RoleNames.ShippingClerk, RoleNames.ReceivingClerk]), default));
        Assert.Equal(ProblemCodesV2.SessionRevoked, (await AuthenticateAsync(token)).Error!.Code);
        Assert.False(regrouped.HasActiveSession);
        Assert.Equal("accountChanged", Text("SELECT RevokeReason FROM LoginSessions WHERE RevokedAt IS NOT NULL ORDER BY Id DESC LIMIT 1"));

        var (second, _) = await LoginAsync("kim", NewPassword);
        Ok(await Users.UpdateAsync(admin, user.Id, Update(regrouped, enabled: false), default));
        Assert.Equal(ProblemCodesV2.SessionRevoked, (await AuthenticateAsync(second)).Error!.Code);
        Assert.Equal(ProblemCodesV2.InvalidCredentials, (await Get<Wms.Application.V2.Auth.V2AuthService>().LoginAsync(new LoginRequest("kim", NewPassword), default)).Error!.Code);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Users.UpdateAsync(admin, user.Id, Update(renamed, displayName: "Stale"), default)).Code);
    }

    [Fact]
    public async Task Password_reset_revokes_sessions_and_defeats_a_login_that_checked_the_old_password()
    {
        var admin = await SignInAsync("admin");
        var user = Ok(await Users.CreateAsync(admin, User("kim", RoleNames.ShippingClerk), default));
        var (token, _) = await LoginAsync("kim", NewPassword);
        var snapshot = (await Get<IV2IdentityStore>().FindUserByNormalizedUsernameAsync("KIM", default))!;
        var hashBefore = Text("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'KIM'");

        Assert.Equal(ProblemCodesV2.ValidationFailed, Fail(await Users.ResetPasswordAsync(admin, user.Id, new ResetPasswordRequest("short"), default)).Code);
        var reset = Ok(await Users.ResetPasswordAsync(admin, user.Id, new ResetPasswordRequest("another password 22", user.Version), default));

        Assert.Equal(user.Version + 1, reset.Version);
        Assert.NotEqual(hashBefore, Text("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'KIM'"));
        Assert.Equal(ProblemCodesV2.SessionRevoked, (await AuthenticateAsync(token)).Error!.Code);
        Assert.Equal("passwordReset", Text("SELECT RevokeReason FROM LoginSessions WHERE RevokedAt IS NOT NULL ORDER BY Id DESC LIMIT 1"));

        // A login that verified the old password before the reset committed opens nothing (B1 expected-version check).
        var stale = await Get<IV2IdentityStore>().StartSessionAsync(
            snapshot.Id, snapshot.Version, RoleNames.ShippingClerk, new string('A', 64), Clock.GetUtcNow(), Clock.GetUtcNow().AddHours(8),
            "replacedByNewLogin", new V2ActivityEntry(Clock.GetUtcNow(), "login", "登录", true, []), default);
        Assert.Null(stale);
        Assert.Equal(ProblemCodesV2.InvalidCredentials, (await Get<Wms.Application.V2.Auth.V2AuthService>().LoginAsync(new LoginRequest("kim", NewPassword), default)).Error!.Code);
        await LoginAsync("kim", "another password 22");

        var logs = Text("SELECT group_concat(coalesce(Changes, '') || Description, '\n') FROM ActivityLogs WHERE Type = 'userResetPassword'");
        Assert.DoesNotContain("another password 22", logs, StringComparison.Ordinal);
        Assert.Equal(ProblemCodesV2.VersionConflict, Fail(await Users.ResetPasswordAsync(admin, user.Id, new ResetPasswordRequest("third password 333", user.Version), default)).Code);
    }

    [Fact]
    public async Task The_last_enabled_administrator_is_protected_also_from_itself_and_from_stale_sessions()
    {
        var (adminToken, admin) = await LoginAsync("admin");
        var adminUser = Ok(await Users.GetAsync("1", default));
        Assert.Equal(ProblemCodesV2.LastAdministrator, Fail(await Users.UpdateAsync(admin, "1", Update(adminUser, enabled: false), default)).Code);
        Assert.Equal(ProblemCodesV2.LastAdministrator, Fail(await Users.UpdateAsync(admin, "1", Update(adminUser, roles: [RoleNames.WarehouseSupervisor]), default)).Code);

        // A second administrator exists but is disabled: still the last enabled one.
        var other = Ok(await Users.CreateAsync(admin, User("boss", RoleNames.SystemAdministrator) with { Enabled = false }, default));
        Assert.Equal(ProblemCodesV2.LastAdministrator, Fail(await Users.UpdateAsync(admin, "1", Update(adminUser, enabled: false), default)).Code);
        other = Ok(await Users.UpdateAsync(admin, other.Id, Update(other, enabled: true), default));

        // Sequential: the first administrator disables the second and revokes its session; a later request made with
        // the second one's old session cannot write. (Truly queued and concurrent writes: the tests below.)
        var boss = await SignInAsync("boss", NewPassword);
        Ok(await Users.UpdateAsync(admin, other.Id, Update(other, enabled: false), default));
        Assert.Equal(ProblemCodesV2.SessionRevoked, Fail(await Users.UpdateAsync(boss, "1", Update(adminUser, enabled: false), default)).Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Users WHERE Enabled = 1 AND Roles LIKE '%systemAdministrator%'"));

        // With another enabled administrator, removing one's own administrator role is allowed and ends one's session.
        other = Ok(await Users.GetAsync(other.Id, default));
        Ok(await Users.UpdateAsync(admin, other.Id, Update(other, enabled: true), default));
        adminUser = Ok(await Users.GetAsync("1", default));
        Ok(await Users.UpdateAsync(admin, "1", Update(adminUser, roles: [RoleNames.WarehouseSupervisor]), default));
        Assert.Equal(ProblemCodesV2.SessionRevoked, (await AuthenticateAsync(adminToken)).Error!.Code);
    }

    [Fact]
    public async Task A_write_queued_behind_the_change_that_revokes_its_session_is_rechecked_and_rejected()
    {
        var (admin, boss, adminUser, bossUser) = await TwoAdministratorsAsync();
        var gate = new GatedStore(Get<IV2CatalogStore>());
        var gated = new V2UserService(
            Get<IV2AdminReader>(), new V2CatalogWriter(gate, Get<IV2IdentityStore>(), Clock), Get<IPasswordHasher>(), Clock);

        var bound = TimeSpan.FromSeconds(30); // every wait is bounded, so a failure can never hang the run
        Task<V2Result<UserResponse>>? bossDisablesAdmin = null;
        Task<V2Result<UserResponse>>? adminDisablesBoss = null;
        try
        {
            // The boss's request takes the shared SQLite writer and pauses inside its transaction ...
            bossDisablesAdmin = gated.UpdateAsync(boss, "1", Update(adminUser, enabled: false), default);
            await gate.Entered.Task.WaitAsync(bound);

            // ... the admin's request (authenticated while its session was still valid) queues on the same writer ...
            adminDisablesBoss = Users.UpdateAsync(admin, bossUser.Id, Update(bossUser, enabled: false), default);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(adminDisablesBoss.IsCompleted, "the second write must wait for the writer held by the first");
        }
        finally
        {
            // Always release the held write transaction, even when an assertion above failed, so it cannot leak.
            gate.Release.TrySetResult();
        }

        // ... the first commits (admin disabled, its session revoked) and only then the second runs its in-transaction check.
        Ok(await bossDisablesAdmin!.WaitAsync(bound));
        Assert.Equal(ProblemCodesV2.SessionRevoked, Fail(await adminDisablesBoss!.WaitAsync(bound)).Code);

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Users WHERE Enabled = 1 AND Roles LIKE '%systemAdministrator%'"));
        Assert.Equal(1L, Scalar($"SELECT Enabled FROM Users WHERE Id = {bossUser.Id}"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM ActivityLogs WHERE Type = 'userUpdate' AND Success = 0 AND FailureCode = 'SESSION_REVOKED' AND ActorRole IS NULL"));
    }

    [Fact]
    public async Task Two_administrators_disabling_each_other_concurrently_leave_exactly_one_enabled()
    {
        var (admin, boss, adminUser, bossUser) = await TwoAdministratorsAsync();

        // No ordering imposed: both requests start together; the shared writer serializes them in either order.
        var results = await Task.WhenAll(
                Task.Run(() => Users.UpdateAsync(admin, bossUser.Id, Update(bossUser, enabled: false), default)),
                Task.Run(() => Users.UpdateAsync(boss, "1", Update(adminUser, enabled: false), default)))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(ProblemCodesV2.SessionRevoked, Assert.Single(results, r => !r.IsSuccess).Failure!.Code);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Users WHERE Enabled = 1 AND Roles LIKE '%systemAdministrator%'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM LoginSessions WHERE LoggedOutAt IS NULL AND RevokedAt IS NULL AND CurrentRole = 'systemAdministrator'"));
    }

    [Fact]
    public async Task Created_accounts_survive_a_restart_and_seeding_never_resets_them()
    {
        var admin = await SignInAsync("admin");
        Ok(await Users.CreateAsync(admin, User("kim", RoleNames.ShippingClerk), default));
        var adminHash = Text("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'");

        await RestartAsync();
        await Get<Wms.Application.V2.V2Seeder>().SeedAsync("a different seed password", default);

        Assert.Equal(adminHash, Text("SELECT PasswordHash FROM Users WHERE NormalizedUsername = 'ADMIN'"));
        await LoginAsync("kim", NewPassword);
        Assert.Equal(5, Ok(await Users.ListAsync(null, null, null, null, null, default)).Items.Count);
        Assert.Single(Ok(await Users.ListAsync("KI", RoleNames.ShippingClerk, true, null, null, default)).Items);
    }

    /// <summary>The seeded admin and a second enabled administrator "boss", both signed in.</summary>
    private async Task<(Wms.Application.V2.Auth.V2SessionPrincipal Admin, Wms.Application.V2.Auth.V2SessionPrincipal Boss, UserResponse AdminUser, UserResponse BossUser)>
        TwoAdministratorsAsync()
    {
        var admin = await SignInAsync("admin");
        var bossUser = Ok(await Users.CreateAsync(admin, User("boss", RoleNames.SystemAdministrator), default));
        var boss = await SignInAsync("boss", NewPassword);
        return (admin, boss, Ok(await Users.GetAsync("1", default)), bossUser);
    }

    /// <summary>Holds the real write transaction (and so the shared writer) until <see cref="Release"/> is set.</summary>
    private sealed class GatedStore(IV2CatalogStore inner) : IV2CatalogStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IV2CatalogWriteScope> BeginWriteAsync(CancellationToken cancellationToken)
        {
            var scope = await inner.BeginWriteAsync(cancellationToken);
            Entered.TrySetResult();
            await Release.Task;
            return scope;
        }
    }

    private static UserCreateRequest User(string username, params string[] roles) =>
        new(username, "Test " + username, roles.Length == 0 ? [RoleNames.ShippingClerk] : roles, true, NewPassword);

    private static UserUpdateRequest Update(
        UserResponse user, string? displayName = null, IReadOnlyList<string>? roles = null, bool? enabled = null) =>
        new(user.Username, displayName ?? user.DisplayName, roles ?? user.Roles, enabled ?? user.Enabled, user.Version);
}
