using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Wms.Application.Auth;
using Wms.Domain.Common;

namespace Wms.Api.Auth;

internal static class AuthorizationSetup
{
    /// <summary>
    /// Every endpoint requires a signed-in user unless it opts out with <c>[AllowAnonymous]</c>. Each of the eight
    /// permissions is a policy satisfied by the roles that hold it (<see cref="RolePermissions"/>).
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (var permission in RolePermissions.AllPermissions)
        {
            options.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RolePermissions.RolesWith(permission)));
        }
    }

    public static UserId? FindUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(WmsClaims.Subject)?.Value, out var id) ? new UserId(id) : (UserId?)null;
}
