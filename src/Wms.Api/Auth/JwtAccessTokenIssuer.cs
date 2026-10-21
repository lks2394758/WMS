using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Wms.Application.Auth;
using Wms.Application.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Api.Auth;

/// <summary>HS256 access token carrying the user ID, username and role. Permissions are derived from the role.</summary>
internal sealed class JwtAccessTokenIssuer(JwtSigning signing) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(UserAccount user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        var expiresAt = now.AddMinutes(signing.Settings.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = signing.Settings.Issuer,
            Audience = signing.Settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = signing.Credentials,
            Claims = new Dictionary<string, object>
            {
                [WmsClaims.Subject] = user.Id.Value.ToString(),
                [WmsClaims.Name] = user.Username,
                [WmsClaims.Role] = WireNames.Of(user.Role),
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }
}
