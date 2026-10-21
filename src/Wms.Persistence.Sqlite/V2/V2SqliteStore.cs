using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.Abstractions;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>
/// v2 SQLite implementation of the identity, seed and health ports. Writes are serialized by an in-process queue and
/// run in one <c>BEGIN IMMEDIATE</c> transaction each (Microsoft.Data.Sqlite's default), so a session change and its
/// activity log entry commit together. Lock timeouts, optimistic version mismatches and unique-index races (two
/// concurrent logins of one account) become <see cref="PersistenceConflictException"/>; nothing is committed then.
/// SQLite evidence only (AGENTS.md rule 4).
/// </summary>
internal sealed partial class V2SqliteStore(IDbContextFactory<WmsV2DbContext> contexts, V2SqliteOptions options)
    : IV2IdentityStore, IV2SeedStore, IV2HealthProbe, IDisposable
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One writer queue for every v2 write of this process (identity and catalog alike).</summary>
    private readonly SemaphoreSlim _writer = new(1, 1);

    private IDbContextFactory<WmsV2DbContext> Contexts => contexts;

    private TimeSpan LockTimeout => TimeSpan.FromSeconds(options.LockTimeoutSeconds);

    public void Dispose() => _writer.Dispose();

    // ---- IV2IdentityStore ----

    public async Task<V2UserRecord?> FindUserByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task<int> GetSessionHoursAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var settings = await db.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == V2SystemSettingsRow.SingletonId, cancellationToken);
        return settings?.SessionHours
            ?? throw new InvalidOperationException("The v2 SystemSettings row is missing; the database was not initialized.");
    }

    public Task<V2SessionRecord?> StartSessionAsync(
        long userId,
        int expectedUserVersion,
        string role,
        string tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        string revokeReason,
        V2ActivityEntry log,
        CancellationToken cancellationToken) =>
        WriteAsync<V2SessionRecord?>(async (db, ct) =>
        {
            // Inside the writer transaction: the password was verified against this exact account version. A password
            // reset, disable or role change committed since then bumps Version, so the stale verification opens nothing.
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null || user.Version != expectedUserVersion || !user.Enabled || !RolesOf(user).Contains(role, StringComparer.Ordinal))
            {
                return null;
            }

            var open = await db.LoginSessions
                .Where(s => s.UserId == userId && s.LoggedOutAt == null && s.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var session in open)
            {
                session.RevokedAt = now;
                session.RevokeReason = revokeReason;
                session.Version++;
            }

            // Revocations first: the partial unique index allows one open session per account.
            await db.SaveChangesAsync(ct);

            var row = new V2LoginSessionRow
            {
                UserId = userId,
                CurrentRole = role,
                TokenHash = tokenHash,
                LoggedInAt = now,
                ExpiresAt = expiresAt,
                Version = 1,
            };
            db.LoginSessions.Add(row);
            db.ActivityLogs.Add(ToRow(log));
            await db.SaveChangesAsync(ct);
            return ToRecord(row);
        }, cancellationToken);

    public async Task<V2AuthenticatedSession?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await db.LoginSessions.AsNoTracking()
            .Where(s => s.TokenHash == tokenHash)
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .SingleOrDefaultAsync(cancellationToken);
        return found is null ? null : new V2AuthenticatedSession(ToRecord(found.Session), ToRecord(found.User));
    }

    public Task<V2SessionChange> SwitchRoleAsync(long sessionId, string role, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
        WriteAsync(async (db, ct) =>
        {
            var session = await FindOpenAsync(db, sessionId, now, ct);
            if (session is null)
            {
                return V2SessionChange.SessionNotOpen;
            }

            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == session.UserId, ct);
            if (!user.Enabled)
            {
                return V2SessionChange.AccountDisabled;
            }

            if (!RolesOf(user).Contains(role, StringComparer.Ordinal))
            {
                return V2SessionChange.RoleNotAssigned;
            }

            session.CurrentRole = role;
            session.Version++;
            db.ActivityLogs.Add(ToRow(log));
            await db.SaveChangesAsync(ct);
            return V2SessionChange.Applied;
        }, cancellationToken);

    public Task<V2SessionChange> EndSessionAsync(long sessionId, DateTimeOffset now, V2ActivityEntry log, CancellationToken cancellationToken) =>
        WriteAsync(async (db, ct) =>
        {
            var session = await FindOpenAsync(db, sessionId, now, ct);
            if (session is null)
            {
                return V2SessionChange.SessionNotOpen;
            }

            session.LoggedOutAt = now;
            session.Version++;
            db.ActivityLogs.Add(ToRow(log));
            await db.SaveChangesAsync(ct);
            return V2SessionChange.Applied;
        }, cancellationToken);

    public Task AppendActivityAsync(V2ActivityEntry entry, CancellationToken cancellationToken) =>
        WriteAsync(async (db, ct) =>
        {
            db.ActivityLogs.Add(ToRow(entry));
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    // ---- IV2SeedStore ----

    public async Task<IReadOnlyCollection<string>> ExistingUsernamesAsync(
        IReadOnlyCollection<string> normalizedUsernames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedUsernames);
        var wanted = normalizedUsernames.ToList();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.NormalizedUsername))
            .Select(u => u.NormalizedUsername)
            .ToListAsync(cancellationToken);
    }

    public Task<V2SeedReport> SeedAsync(V2SeedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WriteAsync(async (db, ct) =>
        {
            var usersInserted = 0;
            foreach (var user in request.Users)
            {
                var normalized = user.Username.Trim().ToUpperInvariant();
                if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
                {
                    continue; // existing accounts and their passwords are never touched
                }

                db.Users.Add(new V2UserRow
                {
                    Username = user.Username.Trim(),
                    NormalizedUsername = normalized,
                    DisplayName = user.DisplayName,
                    PasswordHash = user.PasswordHash,
                    Roles = JsonSerializer.Serialize(user.Roles, Json),
                    Enabled = true,
                    CreatedAt = request.Now,
                    UpdatedAt = request.Now,
                    Version = 1,
                });
                usersInserted++;
            }

            var unitName = request.DefaultUnitName.Trim();
            var normalizedUnit = unitName.ToUpperInvariant();
            var unitInserted = !await db.Units.AnyAsync(u => u.NormalizedName == normalizedUnit, ct);
            if (unitInserted)
            {
                db.Units.Add(new V2UnitRow
                {
                    Name = unitName,
                    NormalizedName = normalizedUnit,
                    IsSystem = true,
                    CreatedAt = request.Now,
                    UpdatedAt = request.Now,
                    Version = 1,
                });
            }

            var settingsInserted = !await db.SystemSettings.AnyAsync(s => s.Id == V2SystemSettingsRow.SingletonId, ct);
            if (settingsInserted)
            {
                db.SystemSettings.Add(new V2SystemSettingsRow
                {
                    Id = V2SystemSettingsRow.SingletonId,
                    DefaultRackLevels = request.DefaultRackLevels,
                    DefaultCellsPerLevel = request.DefaultCellsPerLevel,
                    LowThresholdMicros = request.LowThresholdMicros,
                    HighThresholdMicros = request.HighThresholdMicros,
                    SessionHours = request.SessionHours,
                    UpdatedAt = request.Now,
                    Version = 1,
                });
            }

            await db.SaveChangesAsync(ct);
            return new V2SeedReport(usersInserted, unitInserted, settingsInserted);
        }, cancellationToken);
    }

    // ---- IV2HealthProbe ----

    public async Task<bool> CanReadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.SystemSettings.AsNoTracking().AnyAsync(s => s.Id == V2SystemSettingsRow.SingletonId, cancellationToken);
    }

    // ---- helpers ----

    private static async Task<V2LoginSessionRow?> FindOpenAsync(WmsV2DbContext db, long sessionId, DateTimeOffset now, CancellationToken ct)
    {
        var session = await db.LoginSessions.SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        return session is { LoggedOutAt: null, RevokedAt: null } && session.ExpiresAt > now ? session : null;
    }

    private async Task<T> WriteAsync<T>(Func<WmsV2DbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (!await _writer.WaitAsync(TimeSpan.FromSeconds(options.LockTimeoutSeconds), cancellationToken))
        {
            throw new PersistenceConflictException("The v2 database is busy with another change; nothing was committed.");
        }

        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await work(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw new PersistenceConflictException("A row changed underneath this write; nothing was committed.", e);
        }
        catch (Exception e) when (IsConflict(e))
        {
            throw new PersistenceConflictException("A concurrent write won or the v2 database stayed locked; nothing was committed.", e);
        }
        finally
        {
            _writer.Release();
        }
    }

    internal static bool IsConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked }
                || current is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey })
            {
                return true;
            }
        }

        return false;
    }

    private static string[] RolesOf(V2UserRow row) => JsonSerializer.Deserialize<string[]>(row.Roles, Json) ?? [];

    internal static V2UserRecord ToRecord(V2UserRow row) => new(
        row.Id,
        row.Username,
        row.DisplayName,
        RolesOf(row),
        row.Enabled,
        row.PasswordHash,
        row.Version);

    internal static V2SessionRecord ToRecord(V2LoginSessionRow row) => new(
        row.Id, row.UserId, row.CurrentRole, row.LoggedInAt, row.ExpiresAt, row.LoggedOutAt, row.RevokedAt, row.RevokeReason);

    internal static V2ActivityLogRow ToRow(V2ActivityEntry entry) => new()
    {
        OccurredAt = entry.OccurredAt,
        ActorId = entry.ActorId,
        ActorName = entry.ActorName,
        ActorRole = entry.ActorRole,
        LoginName = entry.LoginName,
        Type = entry.Type,
        Description = entry.Description,
        Success = entry.Success,
        FailureCode = entry.FailureCode,
        FailureReason = entry.FailureReason,
        Subjects = JsonSerializer.Serialize(
            entry.Subjects.Select(s => new SubjectJson(s.Type, s.Id.ToString(CultureInfo.InvariantCulture), s.Label)),
            Json),
        Changes = entry.Changes is null ? null : JsonSerializer.Serialize(entry.Changes, Json),
    };

    /// <summary>Stored subject shape: same as the wire <c>SubjectRef</c> (ID as a string).</summary>
    private sealed record SubjectJson(string Type, string Id, string? Label);
}
