namespace Wms.Persistence.Abstractions.V2;

/// <summary>An account of the v2 database. <see cref="PasswordHash"/> never leaves the application layer.</summary>
public sealed record V2UserRecord(
    long Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool Enabled,
    string PasswordHash,
    int Version);

/// <summary>A persisted login session (LoginSessions row). The token itself is never stored, only its hash.</summary>
public sealed record V2SessionRecord(
    long Id,
    long UserId,
    string CurrentRole,
    DateTimeOffset LoggedInAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LoggedOutAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

/// <summary>A session together with its account, read in one query on every authenticated request.</summary>
public sealed record V2AuthenticatedSession(V2SessionRecord Session, V2UserRecord User);

/// <summary>A structured reference stored in an activity log entry.</summary>
public sealed record V2SubjectRecord(string Type, long Id, string? Label = null);

/// <summary>
/// One append-only ActivityLogs row. Actor members are null when no account was identified (failed login); then
/// <see cref="LoginName"/> holds the masked login name. Never put a password, token or hash into any member.
/// </summary>
public sealed record V2ActivityEntry(
    DateTimeOffset OccurredAt,
    string Type,
    string Description,
    bool Success,
    IReadOnlyList<V2SubjectRecord> Subjects,
    long? ActorId = null,
    string? ActorName = null,
    string? ActorRole = null,
    string? LoginName = null,
    string? FailureCode = null,
    string? FailureReason = null,
    IReadOnlyList<V2FieldChange>? Changes = null);

/// <summary>One changed field of a master-data edit, as display text. Never a password, token, hash or image bytes.</summary>
public sealed record V2FieldChange(string Field, string? Before, string? After);

/// <summary>Outcome of a session change; anything but <see cref="Applied"/> wrote nothing.</summary>
public enum V2SessionChange
{
    Applied,

    /// <summary>The session was logged out, revoked or expired.</summary>
    SessionNotOpen,

    /// <summary>The account is disabled now (the session is no longer valid).</summary>
    AccountDisabled,

    /// <summary>The requested role is not assigned to the account (as re-read in the transaction).</summary>
    RoleNotAssigned,
}

/// <summary>Identity persistence of API v2 (LoginSessions, Users, ActivityLogs, SystemSettings).</summary>
public interface IV2IdentityStore
{
    /// <summary>The account with this normalized (trimmed, upper-invariant) username, or null.</summary>
    Task<V2UserRecord?> FindUserByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken);

    /// <summary>The configured login lifetime in hours (SystemSettings).</summary>
    Task<int> GetSessionHoursAsync(CancellationToken cancellationToken);

    /// <summary>
    /// In one write transaction, first re-reads the account and requires it to be unchanged since the password was
    /// verified (<paramref name="expectedUserVersion"/>), still enabled and still assigned <paramref name="role"/>;
    /// otherwise returns null and writes nothing (no revocation, no session, no log). Then revokes every session of
    /// the account that has neither logged out nor been revoked (reason <paramref name="revokeReason"/>), inserts the
    /// new session and appends <paramref name="log"/>. Throws <see cref="PersistenceConflictException"/> when the write
    /// lock is not obtained or a concurrent login won.
    /// </summary>
    Task<V2SessionRecord?> StartSessionAsync(
        long userId,
        int expectedUserVersion,
        string role,
        string tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        string revokeReason,
        V2ActivityEntry log,
        CancellationToken cancellationToken);

    /// <summary>The session with this token hash and its account, or null.</summary>
    Task<V2AuthenticatedSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// In one write transaction: requires the session to be open (not logged out, not revoked, not expired at
    /// <paramref name="now"/>) and its account to be enabled and assigned <paramref name="role"/> as re-read inside the
    /// transaction; then sets the current role and appends <paramref name="log"/>. Writes nothing otherwise.
    /// </summary>
    Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken);

    /// <summary>
    /// Marks an open session as logged out and appends <paramref name="log"/>, atomically. Ending a session is always
    /// safe, so the account is not re-checked. <see cref="V2SessionChange.SessionNotOpen"/> when already ended or expired.
    /// </summary>
    Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken);

    /// <summary>Appends one log entry in its own transaction (failures, so they survive a rolled-back operation).</summary>
    Task AppendActivityAsync(V2ActivityEntry entry, CancellationToken cancellationToken);
}

/// <summary>One account the v2 seed creates when it is missing. <see cref="PasswordHash"/> is already hashed.</summary>
public sealed record V2SeedUser(string Username, string DisplayName, IReadOnlyList<string> Roles, string PasswordHash);

/// <summary>What the v2 seed should guarantee. Existing rows are never changed.</summary>
public sealed record V2SeedRequest(
    IReadOnlyList<V2SeedUser> Users,
    string DefaultUnitName,
    int DefaultRackLevels,
    int DefaultCellsPerLevel,
    long LowThresholdMicros,
    long HighThresholdMicros,
    int SessionHours,
    DateTimeOffset Now);

public sealed record V2SeedReport(int UsersInserted, bool UnitInserted, bool SettingsInserted);

/// <summary>Insert-missing seed of the v2 database (users by normalized username, the preset unit, the settings row).</summary>
public interface IV2SeedStore
{
    Task<V2SeedReport> SeedAsync(V2SeedRequest request, CancellationToken cancellationToken);

    /// <summary>Normalized usernames among <paramref name="normalizedUsernames"/> that already exist.</summary>
    Task<IReadOnlyCollection<string>> ExistingUsernamesAsync(
        IReadOnlyCollection<string> normalizedUsernames, CancellationToken cancellationToken);
}

/// <summary>Cheap read used by the v2 readiness check: proves the v2 database answers.</summary>
public interface IV2HealthProbe
{
    Task<bool> CanReadAsync(CancellationToken cancellationToken);
}
