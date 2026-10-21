using Wms.Application.Auth;
using Wms.Application.V2.Auth;
using Wms.Application.V2.Catalog;
using Wms.Application.V2.Common;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Admin;

/// <summary>
/// Account administration (decisions §8, §13; contracts/v2/README.md §6), allowed only to a session whose current role
/// is <c>systemAdministrator</c>. No user deletion, no self-registration, no self-service password change.
/// <list type="bullet">
/// <item>username 1-64 without whitespace or control characters, unique by Unicode invariant upper case; display name
/// 1-100; roles non-empty, distinct, fixed values; password 8-256, hashed with the existing PBKDF2 hasher and never
/// logged or returned (not even its hash);</item>
/// <item>a role change or disabling the account, and every password reset, revoke the account's open sessions in the same
/// transaction as the change and bump its version (which also defeats a login that verified the old password);</item>
/// <item>no change may leave the system without an enabled account holding <c>systemAdministrator</c>
/// (<c>LAST_ADMINISTRATOR</c>), the caller's own account included.</item>
/// </list>
/// </summary>
public sealed class V2UserService(IV2AdminReader reader, V2CatalogWriter writer, IPasswordHasher hasher, TimeProvider clock)
{
    public const string RevokedByAccountChange = "accountChanged";
    public const string RevokedByPasswordReset = "passwordReset";
    public const int MinPassword = 8;
    public const int MaxPassword = 256;

    public async Task<V2Result<Page<UserResponse>>> ListAsync(
        string? search, string? role, bool? enabled, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (V2Paging.Parse(cursor, limit, out var after, out var size) is { } invalid)
        {
            return invalid;
        }

        if (role is not null && !V2RolePermissions.IsRole(role))
        {
            return V2Failure.Validation("role", "Unknown role.");
        }

        var text = string.IsNullOrWhiteSpace(search) ? null : V2Text.Normalize(search);
        var users = (await reader.ListUsersAsync(clock.GetUtcNow(), cancellationToken))
            .Where(u => after is null || u.Id > after)
            .Where(u => role is null || u.Roles.Contains(role, StringComparer.Ordinal))
            .Where(u => enabled is null || u.Enabled == enabled)
            .Where(u => text is null
                || V2Text.Normalize(u.Username).Contains(text, StringComparison.Ordinal)
                || V2Text.Normalize(u.DisplayName).Contains(text, StringComparison.Ordinal))
            .Take(size + 1)
            .ToList();
        return new Page<UserResponse>(
            users.Take(size).Select(ToResponse).ToList(),
            users.Count > size ? V2Paging.Encode(users[size - 1].Id) : null);
    }

    public async Task<V2Result<UserResponse>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (V2Text.Id(id, "id", out var userId) is { } invalid)
        {
            return invalid;
        }

        var user = (await reader.ListUsersAsync(clock.GetUtcNow(), cancellationToken)).FirstOrDefault(u => u.Id == userId);
        if (user is null)
        {
            return V2Failure.NotFound("User", V2CatalogMapping.Ref(SubjectTypes.User, userId));
        }

        return ToResponse(user);
    }

    public async Task<V2Result<UserResponse>> CreateAsync(V2SessionPrincipal principal, UserCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badName = V2Text.Name(request.DisplayName, 100, "displayName", out var displayName);
        var rejected = Username(request.Username) ?? badName ?? Roles(request.Roles) ?? Password(request.Password);
        if (rejected is not null)
        {
            return await writer.RejectAsync<UserResponse>(
                principal, PermissionNames.UsersManage, ActivityTypes.UserCreate, "新增账号", [], rejected, cancellationToken);
        }

        var hash = hasher.Hash(request.Password); // outside the writer lock: PBKDF2 is deliberately slow
        var normalized = V2Text.Normalize(request.Username);
        return await writer.RunAsync<UserResponse>(
            principal, PermissionNames.UsersManage, ActivityTypes.UserCreate, $"新增账号 {request.Username}", [],
            async (scope, actor, now) =>
            {
                var users = await scope.ListUsersAsync(cancellationToken);
                if (users.FirstOrDefault(u => V2Text.Normalize(u.Username) == normalized) is { } existing)
                {
                    return UsernameTaken(existing);
                }

                var id = await scope.InsertUserAsync(
                    new V2NewUser(request.Username, normalized, displayName, request.Roles, request.Enabled, hash, now), cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UserCreate, $"新增账号 {request.Username}", true, [new(SubjectTypes.User, id, request.Username)],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes:
                    [
                        new("username", null, request.Username), new("displayName", null, displayName),
                        new("roles", null, string.Join(",", request.Roles)), new("enabled", null, request.Enabled.ToString()),
                    ]));
                return ToResponse((await scope.FindUserSummaryAsync(id, now, cancellationToken))!);
            },
            cancellationToken);
    }

    public async Task<V2Result<UserResponse>> UpdateAsync(
        V2SessionPrincipal principal, string id, UserUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var userId);
        var badName = V2Text.Name(request.DisplayName, 100, "displayName", out var displayName);
        var rejected = badId ?? Username(request.Username) ?? badName ?? Roles(request.Roles);
        if (rejected is not null)
        {
            return await writer.RejectAsync<UserResponse>(
                principal, PermissionNames.UsersManage, ActivityTypes.UserUpdate, "修改账号", Subjects(userId), rejected, cancellationToken);
        }

        var normalized = V2Text.Normalize(request.Username);
        var subject = V2CatalogMapping.Ref(SubjectTypes.User, userId);
        return await writer.RunAsync<UserResponse>(
            principal, PermissionNames.UsersManage, ActivityTypes.UserUpdate, $"修改账号 {request.Username}", Subjects(userId),
            async (scope, actor, now) =>
            {
                var users = await scope.ListUsersAsync(cancellationToken);
                var target = users.FirstOrDefault(u => u.Id == userId);
                if (target is null)
                {
                    return V2Failure.NotFound("User", subject);
                }

                if (target.Version != request.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (users.FirstOrDefault(u => u.Id != userId && V2Text.Normalize(u.Username) == normalized) is { } other)
                {
                    return UsernameTaken(other);
                }

                var after = users.Select(u => u.Id == userId
                    ? (Roles: request.Roles, Enabled: request.Enabled)
                    : (Roles: u.Roles, Enabled: u.Enabled));
                if (!after.Any(u => u.Enabled && u.Roles.Contains(RoleNames.SystemAdministrator, StringComparer.Ordinal)))
                {
                    return LastAdministrator(subject);
                }

                var rolesChanged = !target.Roles.Order(StringComparer.Ordinal).SequenceEqual(request.Roles.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                var disabled = target.Enabled && !request.Enabled;
                if (!await scope.UpdateUserAsync(
                        new V2UserUpdate(userId, target.Version, request.Username, normalized, displayName, request.Roles, request.Enabled, null, now),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                // Same transaction as the change: no request of the old session can act with the old roles afterwards.
                var revoked = rolesChanged || disabled ? await scope.RevokeSessionsAsync(userId, now, RevokedByAccountChange, cancellationToken) : 0;
                var changes = new List<V2FieldChange>();
                AddChange(changes, "username", target.Username, request.Username);
                AddChange(changes, "displayName", target.DisplayName, displayName);
                AddChange(changes, "roles", string.Join(",", target.Roles), string.Join(",", request.Roles));
                AddChange(changes, "enabled", target.Enabled.ToString(), request.Enabled.ToString());
                if (revoked > 0)
                {
                    changes.Add(new V2FieldChange("sessions", $"{revoked} open", "revoked"));
                }

                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UserUpdate, $"修改账号 {request.Username}", true, [new(SubjectTypes.User, userId, request.Username)],
                    actor.UserId, actor.DisplayName, actor.Role, Changes: changes));
                return ToResponse((await scope.FindUserSummaryAsync(userId, now, cancellationToken))!);
            },
            cancellationToken);
    }

    public async Task<V2Result<UserResponse>> ResetPasswordAsync(
        V2SessionPrincipal principal, string id, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var badId = V2Text.Id(id, "id", out var userId);
        var rejected = badId ?? Password(request.Password);
        if (rejected is not null)
        {
            return await writer.RejectAsync<UserResponse>(
                principal, PermissionNames.UsersManage, ActivityTypes.UserResetPassword, "重置密码", Subjects(userId), rejected, cancellationToken);
        }

        var hash = hasher.Hash(request.Password);
        var subject = V2CatalogMapping.Ref(SubjectTypes.User, userId);
        return await writer.RunAsync<UserResponse>(
            principal, PermissionNames.UsersManage, ActivityTypes.UserResetPassword, "重置密码", Subjects(userId),
            async (scope, actor, now) =>
            {
                var target = (await scope.ListUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == userId);
                if (target is null)
                {
                    return V2Failure.NotFound("User", subject);
                }

                if (request.Version is { } version && version != target.Version)
                {
                    return V2Failure.VersionConflict(subject);
                }

                if (!await scope.UpdateUserAsync(
                        new V2UserUpdate(
                            userId, target.Version, target.Username, V2Text.Normalize(target.Username), target.DisplayName, target.Roles,
                            target.Enabled, hash, now),
                        cancellationToken))
                {
                    return V2Failure.VersionConflict(subject);
                }

                var revoked = await scope.RevokeSessionsAsync(userId, now, RevokedByPasswordReset, cancellationToken);
                scope.AddActivity(new V2ActivityEntry(
                    now, ActivityTypes.UserResetPassword, $"重置账号 {target.Username} 的密码", true, [new(SubjectTypes.User, userId, target.Username)],
                    actor.UserId, actor.DisplayName, actor.Role,
                    Changes: [new("password", null, "reset"), new("sessions", $"{revoked} open", "revoked")]));
                return ToResponse((await scope.FindUserSummaryAsync(userId, now, cancellationToken))!);
            },
            cancellationToken);
    }

    public static UserResponse ToResponse(V2UserSummary user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserResponse(
            WireId.Format(user.Id), user.Username, user.DisplayName, user.Roles, user.Enabled, user.HasActiveSession,
            user.CreatedAt, user.UpdatedAt, user.Version);
    }

    private static IReadOnlyList<V2SubjectRecord> Subjects(long userId) => userId > 0 ? [new(SubjectTypes.User, userId)] : [];

    private static void AddChange(List<V2FieldChange> changes, string field, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add(new V2FieldChange(field, before, after));
        }
    }

    private static V2Failure? Username(string? value) =>
        value is { Length: >= 1 and <= 64 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? null
            : V2Failure.Validation("username", "username must be 1-64 characters without spaces or control characters.");

    private static V2Failure? Roles(IReadOnlyList<string>? roles) =>
        roles is { Count: > 0 } && roles.All(V2RolePermissions.IsRole) && roles.Distinct(StringComparer.Ordinal).Count() == roles.Count
            ? null
            : V2Failure.Validation("roles", "roles must be a non-empty list of distinct fixed role names.");

    private static V2Failure? Password(string? value) =>
        value is { Length: >= MinPassword and <= MaxPassword } && !string.IsNullOrWhiteSpace(value)
            ? null
            : V2Failure.Validation("password", $"password must be {MinPassword}-{MaxPassword} characters.");

    private static V2Failure UsernameTaken(V2UserRecord existing) =>
        new(ProblemCodesV2.AlreadyExists, $"The username '{existing.Username}' is taken (case-insensitive).", "username",
            [V2CatalogMapping.Ref(SubjectTypes.User, existing.Id, existing.Username)]);

    private static V2Failure LastAdministrator(SubjectRef subject) =>
        new(
            ProblemCodesV2.LastAdministrator,
            "At least one enabled account must keep the systemAdministrator role.",
            Subjects: [subject]);
}
