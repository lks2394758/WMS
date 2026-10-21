using Wms.Application.Auth;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Auth;

/// <summary>Outcome of checking a bearer token against the persisted session.</summary>
public sealed record V2AuthenticationResult(V2SessionPrincipal? Principal, DomainError? Error);

/// <summary>
/// API v2 identity: one persisted session per account, opaque token (hash stored only), current role kept in the
/// session row and re-validated against the database on every request. No refresh token. Every login, failed login,
/// role switch and logout is appended to the activity log; no password, token or hash is ever logged or returned
/// (the token appears only in the login response).
/// </summary>
public sealed class V2AuthService(IV2IdentityStore store, IPasswordHasher hasher, TimeProvider clock)
{
    public const string RevokedByNewLogin = "replacedByNewLogin";
    private const int MaxPasswordLength = 256;

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Trim().Length > 64)
        {
            return V2Errors.Validation("Username is required (1-64 characters).", "username");
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > MaxPasswordLength)
        {
            return V2Errors.Validation("Password is required (at most 256 characters).", "password");
        }

        if (request.Role is not null && !V2RolePermissions.IsRole(request.Role))
        {
            return V2Errors.Validation("Unknown role.", "role");
        }

        var user = await store.FindUserByNormalizedUsernameAsync(V2Usernames.Normalize(request.Username), cancellationToken);

        // Always verify, against an unusable hash when the account is missing, so timing does not reveal accounts.
        var passwordMatches = hasher.Verify(request.Password, user?.PasswordHash ?? hasher.UnusableHash);
        var now = clock.GetUtcNow();
        if (user is null || !passwordMatches || !user.Enabled)
        {
            await store.AppendActivityAsync(
                new V2ActivityEntry(
                    now, ActivityTypes.Login, "登录失败", false, [],
                    LoginName: V2Usernames.Mask(request.Username),
                    FailureCode: ProblemCodesV2.InvalidCredentials,
                    FailureReason: "用户名或密码错误，或账号已停用"),
                cancellationToken);
            return V2Errors.InvalidCredentials();
        }

        var role = request.Role ?? user.Roles[0];
        if (!user.Roles.Contains(role, StringComparer.Ordinal))
        {
            await store.AppendActivityAsync(
                new V2ActivityEntry(
                    now, ActivityTypes.Login, "登录失败：未分配的身份", false, [UserSubject(user)],
                    ActorId: user.Id, ActorName: user.DisplayName,
                    FailureCode: ProblemCodesV2.RoleNotAssigned,
                    FailureReason: $"账号未分配身份 {role}"),
                cancellationToken);
            return V2Errors.RoleNotAssigned(role);
        }

        var hours = await store.GetSessionHoursAsync(cancellationToken);
        var expiresAt = now.AddHours(hours);
        var token = V2SessionTokens.Create();
        V2SessionRecord? session;
        try
        {
            // The store re-checks user.Version (the snapshot the password was verified against), Enabled and the role
            // inside its write transaction, so a reset or edit committed meanwhile can never yield a new session.
            session = await store.StartSessionAsync(
                user.Id,
                user.Version,
                role,
                V2SessionTokens.Hash(token),
                now,
                expiresAt,
                RevokedByNewLogin,
                new V2ActivityEntry(now, ActivityTypes.Login, "登录", true, [UserSubject(user)], user.Id, user.DisplayName, role),
                cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return V2Errors.ConcurrencyConflict();
        }

        if (session is null)
        {
            // The account changed after the password check: nothing was written. A retry verifies the current state.
            return V2Errors.ConcurrencyConflict();
        }

        return new LoginResponse(token, expiresAt, ToCurrentUser(user.Id, user.Username, user.DisplayName, user.Roles, role));
    }

    /// <summary>
    /// Validates a bearer token against the database: the session must exist, be neither logged out nor revoked, not be
    /// expired, and its account must still be enabled and hold the session's current role.
    /// </summary>
    public async Task<V2AuthenticationResult> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!V2SessionTokens.IsWellFormed(token))
        {
            return new V2AuthenticationResult(null, V2Errors.Unauthenticated());
        }

        var found = await store.FindSessionByTokenHashAsync(V2SessionTokens.Hash(token!), cancellationToken);
        if (found is null)
        {
            return new V2AuthenticationResult(null, V2Errors.Unauthenticated());
        }

        var (session, user) = (found.Session, found.User);
        if (session.LoggedOutAt is not null || session.RevokedAt is not null)
        {
            return new V2AuthenticationResult(null, V2Errors.SessionRevoked());
        }

        if (clock.GetUtcNow() >= session.ExpiresAt)
        {
            return new V2AuthenticationResult(null, V2Errors.SessionExpired());
        }

        if (!user.Enabled || !user.Roles.Contains(session.CurrentRole, StringComparer.Ordinal))
        {
            return new V2AuthenticationResult(null, V2Errors.SessionRevoked());
        }

        return new V2AuthenticationResult(
            new V2SessionPrincipal(session.Id, user.Id, user.Username, user.DisplayName, user.Roles, session.CurrentRole, session.ExpiresAt),
            null);
    }

    public static SessionResponse Describe(V2SessionPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new SessionResponse(
            ToCurrentUser(principal.UserId, principal.Username, principal.DisplayName, principal.Roles, principal.CurrentRole),
            principal.ExpiresAt);
    }

    /// <summary>Changes the current session's role. Same token, same expiry. Switching to the current role is a no-op.</summary>
    public async Task<Result<SessionResponse>> SwitchRoleAsync(
        V2SessionPrincipal principal, SwitchRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (!V2RolePermissions.IsRole(request.Role))
        {
            return V2Errors.Validation("Unknown role.", "role");
        }

        var now = clock.GetUtcNow();
        if (!principal.Roles.Contains(request.Role, StringComparer.Ordinal))
        {
            await store.AppendActivityAsync(
                new V2ActivityEntry(
                    now, ActivityTypes.SwitchRole, "切换身份失败", false, [UserSubject(principal)],
                    principal.UserId, principal.DisplayName, principal.CurrentRole,
                    FailureCode: ProblemCodesV2.RoleNotAssigned,
                    FailureReason: $"账号未分配身份 {request.Role}"),
                cancellationToken);
            return V2Errors.RoleNotAssigned(request.Role);
        }

        if (request.Role == principal.CurrentRole)
        {
            return Describe(principal);
        }

        V2SessionChange change;
        try
        {
            change = await store.SwitchRoleAsync(
                principal.SessionId,
                request.Role,
                now,
                new V2ActivityEntry(
                    now, ActivityTypes.SwitchRole, $"切换身份：{principal.CurrentRole} → {request.Role}", true, [UserSubject(principal)],
                    principal.UserId, principal.DisplayName, request.Role),
                cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return V2Errors.ConcurrencyConflict();
        }

        if (change == V2SessionChange.RoleNotAssigned)
        {
            return V2Errors.RoleNotAssigned(request.Role);
        }

        if (change != V2SessionChange.Applied)
        {
            return V2Errors.SessionRevoked();
        }

        return Describe(principal with { CurrentRole = request.Role });
    }

    public async Task<Result> LogoutAsync(V2SessionPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = clock.GetUtcNow();
        V2SessionChange change;
        try
        {
            change = await store.EndSessionAsync(
                principal.SessionId,
                now,
                new V2ActivityEntry(now, ActivityTypes.Logout, "退出登录", true, [UserSubject(principal)],
                    principal.UserId, principal.DisplayName, principal.CurrentRole),
                cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return Result.Failure(V2Errors.ConcurrencyConflict());
        }

        return change == V2SessionChange.Applied ? Result.Success() : Result.Failure(V2Errors.SessionRevoked());
    }

    private static CurrentUser ToCurrentUser(long id, string username, string displayName, IReadOnlyList<string> roles, string role) =>
        new(WireId.Format(id), username, displayName, roles, role, V2RolePermissions.For(role));

    private static V2SubjectRecord UserSubject(V2UserRecord user) => new(SubjectTypes.User, user.Id, user.Username);

    private static V2SubjectRecord UserSubject(V2SessionPrincipal principal) => new(SubjectTypes.User, principal.UserId, principal.Username);
}
