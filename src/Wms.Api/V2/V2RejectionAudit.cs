using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Monitoring;
using Wms.Persistence.Abstractions.V2;

namespace Wms.Api.V2;

/// <summary>
/// Framework-level audit of rejected v2 mutations (B5b1). The framework rejection sites mark the request explicitly
/// (<see cref="Mark"/>): the model-state factory (400), the authentication challenge (401) and forbid (403), the rate
/// limiter (429), the body-limit middleware and exception handler (413). A 415 from MVC's content-type check has no
/// hook and is recognized by its status (no v2 use case ever answers 415). Use-case failures (written by the services
/// themselves) never set the mark, so they are not logged twice.
/// <para>After the response has been produced, a marked rejection of a known mutation endpoint (allowlist below) is
/// appended as one failed activity entry: allowlisted type, fixed description and reason, the response's stable code,
/// a route ID only when it is a valid positive ID, and the actor only as authenticated against the database in this
/// request (none for 401 or before authentication). Never the body, query, raw path, headers, credentials or exception
/// text. The write is bounded, independent of request cancellation, and a storage failure is only logged by the host;
/// the original response is untouched.</para>
/// </summary>
internal sealed class V2RejectionAuditMiddleware(RequestDelegate next, ILogger<V2RejectionAuditMiddleware> logger)
{
    private static readonly object MarkKey = new();
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private static readonly Dictionary<(string Controller, string Action), (string Type, string? IdSubject)> Commands = new()
    {
        [(nameof(V2AuthController), "Login")] = (ActivityTypes.Login, null),
        [(nameof(V2AuthController), "SwitchRole")] = (ActivityTypes.SwitchRole, null),
        [(nameof(V2AuthController), "Logout")] = (ActivityTypes.Logout, null),
        [(nameof(V2UnitsController), "Create")] = (ActivityTypes.UnitCreate, null),
        [(nameof(V2UnitsController), "Update")] = (ActivityTypes.UnitUpdate, SubjectTypes.Unit),
        [(nameof(V2UnitsController), "Delete")] = (ActivityTypes.UnitDelete, SubjectTypes.Unit),
        [(nameof(V2CategoriesController), "Create")] = (ActivityTypes.CategoryCreate, null),
        [(nameof(V2CategoriesController), "Update")] = (ActivityTypes.CategoryUpdate, SubjectTypes.Category),
        [(nameof(V2CategoriesController), "Delete")] = (ActivityTypes.CategoryDelete, SubjectTypes.Category),
        [(nameof(V2ProductsController), "Create")] = (ActivityTypes.ProductCreate, null),
        [(nameof(V2ProductsController), "Update")] = (ActivityTypes.ProductUpdate, SubjectTypes.Product),
        [(nameof(V2ProductsController), "Archive")] = (ActivityTypes.ProductArchive, SubjectTypes.Product),
        [(nameof(V2BinsController), "Create")] = (ActivityTypes.BinCreate, null),
        [(nameof(V2BinsController), "Update")] = (ActivityTypes.BinUpdate, SubjectTypes.Bin),
        [(nameof(V2BinsController), "Archive")] = (ActivityTypes.BinArchive, SubjectTypes.Bin),
        [(nameof(V2BinsController), "CreateRack")] = (ActivityTypes.RackCreate, null),
        [(nameof(V2UsersController), "Create")] = (ActivityTypes.UserCreate, null),
        [(nameof(V2UsersController), "Update")] = (ActivityTypes.UserUpdate, SubjectTypes.User),
        [(nameof(V2UsersController), "ResetPassword")] = (ActivityTypes.UserResetPassword, SubjectTypes.User),
        [(nameof(V2SettingsController), "Update")] = (ActivityTypes.SettingsUpdate, null),
        [(nameof(V2InventoryController), "Receive")] = (ActivityTypes.Receipt, null),
        [(nameof(V2InventoryController), "Ship")] = (ActivityTypes.Shipment, null),
        [(nameof(V2InventoryController), "Move")] = (ActivityTypes.Movement, null),
        [(nameof(V2InventoryController), "Adjust")] = (ActivityTypes.Adjustment, null),
        [(nameof(V2SerializedItemsController), "Correct")] = (ActivityTypes.SerialCorrect, SubjectTypes.SerializedItem),
        [(nameof(V2CountsController), "Start")] = (ActivityTypes.CountStart, null),
        [(nameof(V2CountsController), "Submit")] = (ActivityTypes.CountSubmit, SubjectTypes.CountTask),
        [(nameof(V2CountsController), "Confirm")] = (ActivityTypes.CountConfirm, SubjectTypes.CountTask),
        [(nameof(V2CountsController), "Recount")] = (ActivityTypes.CountRecount, SubjectTypes.CountTask),
        [(nameof(V2CountsController), "Cancel")] = (ActivityTypes.CountCancel, SubjectTypes.CountTask),
        [(nameof(V2CountsController), "Resolve")] = (ActivityTypes.CountResolve, SubjectTypes.CountTask),
    };

    /// <summary>Marks the request as rejected by the framework with this status and stable code.</summary>
    public static void Mark(HttpContext context, int status, string code) => context.Items[MarkKey] = new Rejection(status, code);

    public async Task InvokeAsync(HttpContext context)
    {
        // Captured before the pipeline runs: the exception handler clears the endpoint and route values.
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action is null || !Commands.TryGetValue((action.ControllerTypeInfo.Name, action.ActionName), out var command))
        {
            await next(context); // reads, health checks and unknown routes are not audited
            return;
        }

        var routeId = RouteId(context);
        await next(context);

        if (context.Items.TryGetValue(MarkKey, out var value) && value is Rejection mark && mark.Status == context.Response.StatusCode)
        {
            await AuditAsync(context, command, routeId, mark.Code);
        }
        else if (context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType)
        {
            await AuditAsync(context, command, routeId, ProblemCodesV2.ValidationFailed);
        }
    }

    private async Task AuditAsync(HttpContext context, (string Type, string? IdSubject) command, long? routeId, string code)
    {

        try
        {
            // Only the identity validated against the database in this request; a 401 never names an account.
            var principal = context.Response.StatusCode == StatusCodes.Status401Unauthorized
                ? null
                : V2SessionAuthenticationHandler.PrincipalOf(context);
            IReadOnlyList<V2SubjectRecord> subjects = command.IdSubject is { } subjectType && routeId is { } id
                ? [new V2SubjectRecord(subjectType, id)]
                : [];
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var entry = new V2ActivityEntry(
                now, command.Type, "请求被拒绝", false, subjects,
                principal?.UserId, principal?.DisplayName, principal?.CurrentRole,
                FailureCode: code, FailureReason: ReasonOf(context.Response.StatusCode));

            using var timeout = new CancellationTokenSource(WriteTimeout); // not the request token: an aborted client is still audited
            await context.RequestServices.GetRequiredService<IV2IdentityStore>().AppendActivityAsync(entry, timeout.Token);
        }
#pragma warning disable CA1031 // the audit must never replace or alter the original rejection
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning("Rejected v2 request {Type} could not be audited ({Error}).", command.Type, exception.GetType().Name);
        }
    }

    private static long? RouteId(HttpContext context) =>
        context.GetRouteValue("id") is string text && text.Length is > 0 and <= 19
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    private static string ReasonOf(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "请求格式无效或字段无法绑定",
        StatusCodes.Status401Unauthorized => "未登录或会话无效",
        StatusCodes.Status403Forbidden => "当前身份无此权限",
        StatusCodes.Status413PayloadTooLarge => "请求体超过大小限制",
        StatusCodes.Status415UnsupportedMediaType => "不支持的请求内容类型",
        StatusCodes.Status429TooManyRequests => "请求过于频繁",
        _ => "请求被拒绝",
    };

    private sealed record Rejection(int Status, string Code);
}
