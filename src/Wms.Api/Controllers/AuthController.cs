using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wms.Api.Auth;
using Wms.Api.Validation;
using Wms.Application.Auth;
using Wms.Application.Common;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;

namespace Wms.Api.Controllers;

[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth) : WmsControllerBase
{
    /// <summary>Signs in and returns a 15-minute access token and a rotating refresh token for an 8-hour session.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var invalid = RequestRules.Validate(request);
        return invalid.IsEmpty ? FromResult(await auth.LoginAsync(request, cancellationToken)) : ValidationFailure(invalid);
    }

    /// <summary>Exchanges a refresh token for new tokens. The presented token is revoked; reusing it ends the session.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)]
    public async Task<ActionResult<LoginResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var invalid = RequestRules.Validate(request);
        return invalid.IsEmpty ? FromResult(await auth.RefreshAsync(request, cancellationToken)) : ValidationFailure(invalid);
    }

    /// <summary>The signed-in user, role and permissions.</summary>
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<WmsProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var userId = User.FindUserId();
        if (userId is null)
        {
            return Failure(ApplicationErrors.Unauthenticated("The access token has no user."));
        }

        return FromResult(await auth.GetCurrentUserAsync(userId.Value, cancellationToken));
    }
}
