using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Application.V2.Auth;
using Wms.Contracts.V2.Auth;

namespace Wms.Api.V2;

/// <summary>
/// <c>/api/v2/auth</c> (contracts/v2/README.md §3). Internal on purpose: only the v2 host registers it
/// (<see cref="V2ControllerFeatureProvider"/>), so the v1 host and its OpenAPI document never see it.
/// </summary>
[ApiController]
[Route("api/v2/auth")]
internal sealed class V2AuthController(V2AuthService auth) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : V2Problems.Result(HttpContext, result.Error!);
    }

    [HttpGet("me")]
    public IActionResult Me() =>
        V2SessionAuthenticationHandler.PrincipalOf(HttpContext) is { } principal
            ? Ok(V2AuthService.Describe(principal))
            : V2Problems.Result(HttpContext, V2Errors.Unauthenticated());

    [HttpPost("switch-role")]
    public async Task<IActionResult> SwitchRole([FromBody] SwitchRoleRequest request, CancellationToken cancellationToken)
    {
        if (V2SessionAuthenticationHandler.PrincipalOf(HttpContext) is not { } principal)
        {
            return V2Problems.Result(HttpContext, V2Errors.Unauthenticated());
        }

        var result = await auth.SwitchRoleAsync(principal, request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : V2Problems.Result(HttpContext, result.Error!);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (V2SessionAuthenticationHandler.PrincipalOf(HttpContext) is not { } principal)
        {
            return V2Problems.Result(HttpContext, V2Errors.Unauthenticated());
        }

        var result = await auth.LogoutAsync(principal, cancellationToken);
        return result.IsSuccess ? NoContent() : V2Problems.Result(HttpContext, result.Error!);
    }
}
