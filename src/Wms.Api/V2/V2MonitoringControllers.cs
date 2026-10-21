using Microsoft.AspNetCore.Mvc;
using Wms.Application.V2.Monitoring;

namespace Wms.Api.V2;

/// <summary><c>/api/v2/alerts</c> (contracts/v2/README.md §2, §10): read-only. Binding and result mapping only.</summary>
[Route("api/v2/alerts")]
internal sealed class V2AlertsController(V2MonitoringService monitoring) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] string? binId,
        [FromQuery] string? productId,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await monitoring.ListAlertsAsync(status, type, binId, productId, cursor, limit, cancellationToken));
}

/// <summary><c>/api/v2/activity-logs</c> (contracts/v2/README.md §2, §10): read-only. Binding and result mapping only.</summary>
[Route("api/v2/activity-logs")]
internal sealed class V2ActivityLogsController(V2MonitoringService monitoring) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? actorId,
        [FromQuery] string? productId,
        [FromQuery] string? binId,
        [FromQuery] string? type,
        [FromQuery] bool? success,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken) =>
        Reply(await monitoring.ListActivityAsync(search, from, to, actorId, productId, binId, type, success, cursor, limit, cancellationToken));
}

/// <summary><c>/api/v2/dashboard</c> (contracts/v2/README.md §2, §10): read-only. Binding and result mapping only.</summary>
[Route("api/v2/dashboard")]
internal sealed class V2DashboardController(V2MonitoringService monitoring) : V2ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? scope, CancellationToken cancellationToken) =>
        Principal is { } principal ? Reply(await monitoring.GetDashboardAsync(principal, scope, cancellationToken)) : Unauthenticated();
}
