using Wms.Domain.Common;
using Wms.Domain.Identity;

namespace Wms.Persistence.Abstractions;

/// <summary>A user who can sign in. <see cref="PasswordHash"/> is a self-describing hash, never the password.</summary>
public sealed record UserAccount(
    UserId Id,
    string Username,
    string DisplayName,
    UserRole Role,
    string PasswordHash,
    bool IsActive = true);

/// <summary>
/// One refresh token, stored only as a hash. Tokens issued by one login share <see cref="FamilyId"/>; each refresh
/// revokes the presented token and issues its replacement.
/// </summary>
public sealed record RefreshTokenRecord(
    Guid Id,
    Guid FamilyId,
    UserId UserId,
    string TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt = null,
    Guid? ReplacedById = null);

public interface IUserStore
{
    /// <summary>Case-insensitive lookup.</summary>
    Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<UserAccount?> FindByIdAsync(UserId id, CancellationToken cancellationToken);
}

public interface IRefreshTokenStore
{
    Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken);

    Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically revokes <paramref name="currentId"/> and stores <paramref name="replacement"/>. Returns false, and
    /// stores nothing, if the current token is unknown or already revoked.
    /// </summary>
    Task<bool> TryRotateAsync(Guid currentId, RefreshTokenRecord replacement, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revokes every unrevoked token of the family (sign-out of that session).</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);
}
