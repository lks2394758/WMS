using System.Security.Cryptography;
using System.Text;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Domain.Common;

namespace Wms.Application.V2.Auth;

/// <summary>The fixed v2 role → permission matrix (contracts/v2/README.md §1.1). Only the current role counts.</summary>
public static class V2RolePermissions
{
    private static readonly IReadOnlyList<string> Administrator = PermissionNames.All;

    private static readonly IReadOnlyList<string> Supervisor =
    [
        PermissionNames.CatalogWrite, PermissionNames.InventoryReceive, PermissionNames.InventoryShip,
        PermissionNames.InventoryMove, PermissionNames.InventoryAdjust, PermissionNames.InventoryCount,
        PermissionNames.SerialNumbersCorrect,
    ];

    private static readonly IReadOnlyList<string> Receiving = [PermissionNames.InventoryReceive];
    private static readonly IReadOnlyList<string> Shipping = [PermissionNames.InventoryShip];

    public static bool IsRole(string? role) => role is not null && RoleNames.All.Contains(role, StringComparer.Ordinal);

    public static IReadOnlyList<string> For(string role) => role switch
    {
        RoleNames.SystemAdministrator => Administrator,
        RoleNames.WarehouseSupervisor => Supervisor,
        RoleNames.ReceivingClerk => Receiving,
        RoleNames.ShippingClerk => Shipping,
        _ => [],
    };

    public static bool Grants(string role, string permission) => For(role).Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Opaque session credentials: 32 random bytes, base64url without padding (43 characters). Only the SHA-256 hash
/// (upper-case hex) is stored. A high-entropy random token needs no salt or slow hash.
/// </summary>
public static class V2SessionTokens
{
    public const int TokenLength = 43;

    public static string Create()
    {
        var text = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return text.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsWellFormed(string? token) =>
        token is not null && token.Length == TokenLength && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}

/// <summary>Username rules shared by login, seeding and (later) user management.</summary>
public static class V2Usernames
{
    public static string Normalize(string username) => username.Trim().ToUpperInvariant();

    /// <summary>A login name safe for the activity log: at most two characters kept, the rest masked.</summary>
    public static string Mask(string? username)
    {
        var trimmed = (username ?? string.Empty).Trim();
        var keep = trimmed.Length switch
        {
            0 => 0,
            <= 3 => 1,
            _ => 2,
        };
        return string.Concat(trimmed.AsSpan(0, keep), "***");
    }
}

/// <summary>The authenticated v2 session of the current request, freshly validated against the database.</summary>
public sealed record V2SessionPrincipal(
    long SessionId,
    long UserId,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string CurrentRole,
    DateTimeOffset ExpiresAt)
{
    public bool Has(string permission) => V2RolePermissions.Grants(CurrentRole, permission);
}

/// <summary>Errors raised by the v2 use cases; codes are <see cref="ProblemCodesV2"/>.</summary>
public static class V2Errors
{
    public static DomainError Validation(string message, string field) => new(ProblemCodesV2.ValidationFailed, message, field);

    public static DomainError Unauthenticated() => new(ProblemCodesV2.Unauthenticated, "Sign in to use this endpoint.");

    public static DomainError InvalidCredentials() =>
        new(ProblemCodesV2.InvalidCredentials, "The username or password is wrong, or the account is disabled.");

    public static DomainError SessionExpired() => new(ProblemCodesV2.SessionExpired, "The session has expired. Sign in again.");

    public static DomainError SessionRevoked() =>
        new(ProblemCodesV2.SessionRevoked, "The session has ended (logout, a newer login or an account change). Sign in again.");

    public static DomainError RoleNotAssigned(string role) =>
        new(ProblemCodesV2.RoleNotAssigned, $"The account is not assigned the role '{role}'.");

    public static DomainError ConcurrencyConflict() =>
        new(ProblemCodesV2.ConcurrencyConflict, "Another change to this account was committed at the same time. Retry.");
}
