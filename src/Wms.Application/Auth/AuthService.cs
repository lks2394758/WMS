using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Wms.Application.Common;
using Wms.Contracts.Auth;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Auth;

/// <summary>
/// Login, rotating refresh and current user (guide §7.2). Access tokens live 15 minutes (set by the issuer); a login
/// session lives 8 hours from sign-in. Each refresh revokes the presented token; presenting a revoked token again is
/// treated as theft and ends the whole session.
/// </summary>
public sealed class AuthService(
    IUserStore users,
    IRefreshTokenStore refreshTokens,
    IAuditLog audit,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokens,
    TimeProvider clock)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private const string InvalidCredentials = "Username or password is incorrect.";
    private const string InvalidRefreshToken = "The refresh token is invalid or expired. Sign in again.";

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return DomainErrors.Validation("username is required.", "username");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return DomainErrors.Validation("password is required.", "password");
        }

        var username = request.Username.Trim();
        var now = clock.GetUtcNow();
        var user = await users.FindByUsernameAsync(username, cancellationToken);

        // Always run one hash verification so response time does not reveal whether the username exists.
        var passwordMatches = passwordHasher.Verify(request.Password, user?.PasswordHash ?? passwordHasher.UnusableHash);
        if (user is not { IsActive: true } || !passwordMatches)
        {
            await audit.AppendAsync(
                new AuditEntry(Guid.CreateVersion7(), now, user?.Id, "auth.login.failed", "User", user?.Id.ToString(), $"username={username}"),
                cancellationToken);
            return ApplicationErrors.Unauthenticated(InvalidCredentials);
        }

        var (token, record) = NewRefreshToken(user.Id, Guid.CreateVersion7(), now, now + SessionLifetime);
        await refreshTokens.AddAsync(record, cancellationToken);
        await audit.AppendAsync(
            new AuditEntry(Guid.CreateVersion7(), now, user.Id, "auth.login.succeeded", "User", user.Id.ToString()),
            cancellationToken);

        return BuildResponse(user, token, record, now);
    }

    public async Task<Result<LoginResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return DomainErrors.Validation("refreshToken is required.", "refreshToken");
        }

        var now = clock.GetUtcNow();
        var current = await refreshTokens.FindByHashAsync(HashToken(request.RefreshToken), cancellationToken);
        if (current is null)
        {
            return ApplicationErrors.Unauthenticated(InvalidRefreshToken);
        }

        if (current.RevokedAt is not null)
        {
            await EndSessionAsync(current, now, "auth.refresh.reused", cancellationToken);
            return ApplicationErrors.Unauthenticated(InvalidRefreshToken);
        }

        if (current.ExpiresAt <= now)
        {
            return ApplicationErrors.Unauthenticated(InvalidRefreshToken);
        }

        var user = await users.FindByIdAsync(current.UserId, cancellationToken);
        if (user is not { IsActive: true })
        {
            await EndSessionAsync(current, now, "auth.refresh.userInactive", cancellationToken);
            return ApplicationErrors.Unauthenticated(InvalidRefreshToken);
        }

        // The replacement keeps the session's absolute expiry: refreshing never extends a session past 8 hours.
        var (token, replacement) = NewRefreshToken(user.Id, current.FamilyId, now, current.ExpiresAt);
        if (!await refreshTokens.TryRotateAsync(current.Id, replacement, now, cancellationToken))
        {
            // Another request rotated the same token first: same treatment as reuse.
            await EndSessionAsync(current, now, "auth.refresh.reused", cancellationToken);
            return ApplicationErrors.Unauthenticated(InvalidRefreshToken);
        }

        return BuildResponse(user, token, replacement, now);
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken);
        if (user is not { IsActive: true })
        {
            return ApplicationErrors.Unauthenticated("The signed-in user no longer exists or is disabled.");
        }

        return ToCurrentUser(user);
    }

    /// <summary>Refresh tokens are stored only as this hash.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static CurrentUserResponse ToCurrentUser(UserAccount user) =>
        new(user.Id.Value, user.Username, user.DisplayName, WireNames.Of(user.Role), RolePermissions.For(user.Role));

    private LoginResponse BuildResponse(UserAccount user, string refreshToken, RefreshTokenRecord record, DateTimeOffset now)
    {
        var access = accessTokens.Issue(user, now);
        return new LoginResponse(access.Value, access.ExpiresAt, refreshToken, record.ExpiresAt, ToCurrentUser(user));
    }

    private async Task EndSessionAsync(RefreshTokenRecord token, DateTimeOffset now, string action, CancellationToken cancellationToken)
    {
        await refreshTokens.RevokeFamilyAsync(token.FamilyId, now, cancellationToken);
        await audit.AppendAsync(
            new AuditEntry(Guid.CreateVersion7(), now, token.UserId, action, "RefreshTokenFamily", token.FamilyId.ToString()),
            cancellationToken);
    }

    private static (string Token, RefreshTokenRecord Record) NewRefreshToken(
        UserId userId, Guid familyId, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, new RefreshTokenRecord(Guid.CreateVersion7(), familyId, userId, HashToken(token), now, expiresAt));
    }
}
