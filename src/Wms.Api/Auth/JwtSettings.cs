namespace Wms.Api.Auth;

/// <summary>Configuration section <c>Jwt</c>.</summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "wms-api";

    public string Audience { get; set; } = "wms-clients";

    /// <summary>
    /// Base64 HMAC-SHA256 key of at least 32 bytes. Never stored in source or appsettings files: use user-secrets in
    /// Development and a secret store or environment variable (<c>Jwt__SigningKey</c>) elsewhere.
    /// </summary>
    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;
}

/// <summary>Claim types written into and read from access tokens.</summary>
internal static class WmsClaims
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Role = "role";
}
