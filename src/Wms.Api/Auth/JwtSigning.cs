using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Wms.Api.Auth;

/// <summary>
/// The signing key, resolved once. Outside Development a configured key is mandatory; in Development a missing key is
/// replaced by a random one that lives as long as the process.
/// </summary>
internal sealed class JwtSigning
{
    private const int MinimumKeyBytes = 32;

    public JwtSigning(IOptions<JwtSettings> options, IHostEnvironment environment, ILogger<JwtSigning> logger)
    {
        Settings = options.Value;
        if (Settings.AccessTokenMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 60.");
        }

        byte[] key;
        if (!string.IsNullOrWhiteSpace(Settings.SigningKey))
        {
            try
            {
                key = Convert.FromBase64String(Settings.SigningKey);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("Jwt:SigningKey must be base64.");
            }
        }
        else if (environment.IsDevelopment())
        {
            key = RandomNumberGenerator.GetBytes(MinimumKeyBytes);
            logger.LogWarning(
                "Jwt:SigningKey is not configured; using an ephemeral development key. Tokens stop working when the process restarts.");
        }
        else
        {
            throw new InvalidOperationException("Jwt:SigningKey must be configured outside Development.");
        }

        if (key.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException($"Jwt:SigningKey must be at least {MinimumKeyBytes} bytes.");
        }

        Key = new SymmetricSecurityKey(key);
        Credentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256);
    }

    public JwtSettings Settings { get; }

    public SymmetricSecurityKey Key { get; }

    public SigningCredentials Credentials { get; }

    public static void ConfigureBearer(JwtBearerOptions options, JwtSigning signing)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = signing.Settings.Issuer,
            ValidAudience = signing.Settings.Audience,
            IssuerSigningKey = signing.Key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = WmsClaims.Name,
            RoleClaimType = WmsClaims.Role,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}
