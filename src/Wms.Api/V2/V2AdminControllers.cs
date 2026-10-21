using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wms.Application.V2.Admin;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;

namespace Wms.Api.V2;

/// <summary><c>/api/v2/bins</c> and <c>/api/v2/bins/racks</c> (contracts/v2/README.md §5).</summary>
[Route("api/v2/bins")]
internal sealed class V2BinsController(V2BinService bins) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? zone,
        [FromQuery] string? rack,
        [FromQuery] string? level,
        [FromQuery] string? productId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await bins.ListAsync(search, zone, rack, level, productId, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await bins.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Create([FromBody] BinWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await bins.CreateAsync(principal, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPut("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Update(string id, [FromBody] BinWriteRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await bins.UpdateAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpDelete("{id}")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> Archive(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteRequest? request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await bins.ArchiveAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpPost("racks")]
    [Authorize(Policy = PermissionNames.CatalogWrite)]
    public async Task<IActionResult> CreateRack([FromBody] RackCreateRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await bins.CreateRackAsync(principal, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();
}

/// <summary><c>/api/v2/users</c> (contracts/v2/README.md §6): current role systemAdministrator only, reads included.</summary>
[Route("api/v2/users")]
[Authorize(Policy = PermissionNames.UsersManage)]
internal sealed class V2UsersController(V2UserService users) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search, [FromQuery] string? role, [FromQuery] bool? enabled, [FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await users.ListAsync(search, role, enabled, cursor, limit, cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken) =>
        Reply(await users.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserCreateRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal
            ? Reply(await users.CreateAsync(principal, request, cancellationToken), StatusCodes.Status201Created)
            : Unauthenticated();

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UserUpdateRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await users.UpdateAsync(principal, id, request, cancellationToken)) : Unauthenticated();

    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await users.ResetPasswordAsync(principal, id, request, cancellationToken)) : Unauthenticated();
}

/// <summary><c>/api/v2/settings</c> (contracts/v2/README.md §6): any signed-in user reads; systemAdministrator changes.</summary>
[Route("api/v2/settings")]
internal sealed class V2SettingsController(V2SettingsService settings) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Reply(await settings.GetAsync(cancellationToken));

    [HttpPut]
    [Authorize(Policy = PermissionNames.SettingsManage)]
    public async Task<IActionResult> Update([FromBody] SettingsUpdateRequest request, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await settings.UpdateAsync(principal, request, cancellationToken)) : Unauthenticated();
}
