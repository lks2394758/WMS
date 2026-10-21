using Wms.Application.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Application.V2.Common;

/// <summary>The actor of a write as re-read inside its transaction (current session role, not the role list).</summary>
public sealed record V2Actor(long UserId, string DisplayName, string Role);

/// <summary>
/// Runs one catalog write:
/// <list type="number">
/// <item>opens the write transaction (waiting for the v2 writer queue);</item>
/// <item>only then reads the clock, so a session that expired while the request waited is seen as expired, and that
/// time is used for the change and every log entry;</item>
/// <item>re-validates the caller's session inside the transaction: open, not expired, account enabled, current role still
/// assigned and granting the permission (the current role only, never the union of roles);</item>
/// <item>runs the work, which adds its success log entry to the same transaction, and commits only on success.</item>
/// </list>
/// A failure (authorization, request validation or business rule) rolls everything back and is then appended to the
/// activity log in its own transaction, so it survives the rollback. The failure entry names the identity that was
/// actually verified inside the transaction: the account and its current session role when the session is still valid;
/// the account without a role when the session is no longer valid (revoked, expired, disabled, role removed); no actor at
/// all when the session row cannot be found. It never reuses the role from the request's earlier authentication. No
/// credential, request body or image content is logged.
/// </summary>
public sealed class V2CatalogWriter(IV2CatalogStore store, IV2IdentityStore identity, TimeProvider clock)
{
    public async Task<V2Result<T>> RunAsync<T>(
        V2SessionPrincipal principal,
        string permission,
        string activityType,
        string description,
        IReadOnlyList<V2SubjectRecord> subjects,
        Func<IV2CatalogWriteScope, V2Actor, DateTimeOffset, Task<V2Result<T>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(work);
        V2Result<T> result;
        DateTimeOffset now;
        LogIdentity verified;
        await using (var scope = await store.BeginWriteAsync(cancellationToken))
        {
            now = clock.GetUtcNow(); // after the writer queue: the time the change really happens
            var check = await AuthorizeAsync(scope, principal, permission, now, cancellationToken);
            verified = check.Identity;
            result = check.Failure is { } denied ? denied : await work(scope, check.Actor!, now);
            if (result.IsSuccess)
            {
                await scope.CommitAsync(cancellationToken);
            }
        }

        if (result.Failure is { } failure)
        {
            await identity.AppendActivityAsync(
                new V2ActivityEntry(
                    now, activityType, description + "失败", false, subjects,
                    verified.UserId, verified.DisplayName, verified.Role,
                    FailureCode: failure.Code, FailureReason: failure.Message),
                cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Records a rejected write whose request failed the use case's own validation (bad ID, name, version, key, image,
    /// ...). The session is still re-validated in a write transaction so the entry names the verified identity; nothing
    /// is changed. When the session no longer authorizes the write, that authorization failure is returned instead.
    /// </summary>
    public Task<V2Result<T>> RejectAsync<T>(
        V2SessionPrincipal principal,
        string permission,
        string activityType,
        string description,
        IReadOnlyList<V2SubjectRecord> subjects,
        V2Failure failure,
        CancellationToken cancellationToken) =>
        RunAsync<T>(
            principal, permission, activityType, description, subjects,
            (_, _, _) => Task.FromResult<V2Result<T>>(failure),
            cancellationToken);

    private static async Task<(V2Actor? Actor, V2Failure? Failure, LogIdentity Identity)> AuthorizeAsync(
        IV2CatalogWriteScope scope, V2SessionPrincipal principal, string permission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fresh = await scope.FindSessionAsync(principal.SessionId, cancellationToken);
        if (fresh is null)
        {
            return (null, V2Failure.From(V2Errors.Unauthenticated()), new LogIdentity(null, null, null));
        }

        var (session, user) = (fresh.Session, fresh.User);
        var withoutRole = new LogIdentity(user.Id, user.DisplayName, null);
        if (session.LoggedOutAt is not null || session.RevokedAt is not null || !user.Enabled
            || !user.Roles.Contains(session.CurrentRole, StringComparer.Ordinal))
        {
            return (null, V2Failure.From(V2Errors.SessionRevoked()), withoutRole);
        }

        if (now >= session.ExpiresAt)
        {
            return (null, V2Failure.From(V2Errors.SessionExpired()), withoutRole);
        }

        var current = new LogIdentity(user.Id, user.DisplayName, session.CurrentRole);
        if (!V2RolePermissions.Grants(session.CurrentRole, permission))
        {
            return (null, new V2Failure(ProblemCodesV2.Forbidden, "The current role does not have this permission."), current);
        }

        return (new V2Actor(user.Id, user.DisplayName, session.CurrentRole), null, current);
    }

    private sealed record LogIdentity(long? UserId, string? DisplayName, string? Role);
}
