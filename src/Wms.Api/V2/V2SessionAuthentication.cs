using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Wms.Application.V2.Auth;
using Wms.Contracts.V2.Common;
using Wms.Domain.Common;

namespace Wms.Api.V2;

internal static class V2Claims
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string Role = "role";
    public const string Name = "name";
    public const string Permission = "permission";
}

/// <summary>
/// Authentication of API v2: <c>Authorization: Bearer &lt;opaque token&gt;</c> checked against the persisted session on
/// every request (<see cref="V2AuthService.AuthenticateAsync"/>), so logout, a newer login, expiry or an account change
/// take effect at once. A failure answers 401 with <c>UNAUTHENTICATED</c>, <c>SESSION_EXPIRED</c> or
/// <c>SESSION_REVOKED</c>; a missing permission answers 403 <c>FORBIDDEN</c>. The token is never logged.
/// </summary>
internal sealed class V2SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WmsV2Session";
    private const string BearerPrefix = "Bearer ";
    private static readonly object FailureKey = new();

    /// <summary>The validated session of the current request, or null.</summary>
    public static V2SessionPrincipal? PrincipalOf(HttpContext context) =>
        context.Items.TryGetValue(typeof(V2SessionPrincipal), out var value) ? value as V2SessionPrincipal : null;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Context.Items[FailureKey] = V2Errors.Unauthenticated();
            return AuthenticateResult.Fail(ProblemCodesV2.Unauthenticated);
        }

        var auth = Context.RequestServices.GetRequiredService<V2AuthService>();
        var result = await auth.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (result.Principal is not { } principal)
        {
            Context.Items[FailureKey] = result.Error;
            return AuthenticateResult.Fail(result.Error?.Code ?? ProblemCodesV2.Unauthenticated);
        }

        Context.Items[typeof(V2SessionPrincipal)] = principal;
        var claims = new List<Claim>
        {
            new(V2Claims.Subject, principal.UserId.ToString(CultureInfo.InvariantCulture)),
            new(V2Claims.SessionId, principal.SessionId.ToString(CultureInfo.InvariantCulture)),
            new(V2Claims.Role, principal.CurrentRole),
            new(V2Claims.Name, principal.Username),
        };
        claims.AddRange(V2RolePermissions.For(principal.CurrentRole).Select(p => new Claim(V2Claims.Permission, p)));
        var identity = new ClaimsIdentity(claims, SchemeName, V2Claims.Name, V2Claims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items[FailureKey] as DomainError ?? V2Errors.Unauthenticated();
        V2RejectionAuditMiddleware.Mark(Context, StatusCodes.Status401Unauthorized, error.Code);
        return V2Problems.WriteAsync(Context, error);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        V2RejectionAuditMiddleware.Mark(Context, StatusCodes.Status403Forbidden, ProblemCodesV2.Forbidden);
        return V2Problems.WriteAsync(Context, new DomainError(ProblemCodesV2.Forbidden, "The current role does not have this permission."));
    }
}
