using Wms.Contracts.V2.Common;

namespace Wms.Contracts.V2.Monitoring;

/// <summary>Values captured when the alert opened; never rewritten by later changes. Which members are set depends on the type.</summary>
public sealed record AlertSnapshot(
    string? Quantity = null,
    string? Capacity = null,
    string? OccupancyPercent = null,
    string? LowThreshold = null,
    string? HighThreshold = null,
    string? UnitName = null,
    int? DecimalPlaces = null,
    string? Sku = null,
    int? Round = null,
    string? BookQuantity = null,
    string? ActualQuantity = null,
    string? Difference = null);

/// <summary><c>GET /api/v2/alerts</c> row. Alerts are read-only: they close automatically or by a count resolve.</summary>
public sealed record AlertResponse(
    string Id,
    string Type,
    string Status,
    string BinId,
    string BinCode,
    string ProductId,
    string ProductName,
    string Message,
    DateTimeOffset CreatedAt,
    AlertSnapshot Trigger,
    string? CountTaskId = null,
    string? CountResultId = null,
    DateTimeOffset? ResolvedAt = null,
    string? ResolutionNote = null);

/// <summary>A changed field on a master-data edit. Passwords and credentials are never recorded.</summary>
public sealed record FieldChange(string Field, string? Before = null, string? After = null);

/// <summary>
/// <c>GET /api/v2/activity-logs</c> row (append-only). Actor members are absent when no account was identified (failed
/// login); <see cref="LoginName"/> is then the masked login name. <see cref="Subjects"/> lists every related object.
/// </summary>
public sealed record ActivityLogResponse(
    string Id,
    DateTimeOffset OccurredAt,
    string Type,
    string Description,
    bool Success,
    IReadOnlyList<SubjectRef> Subjects,
    string? ActorId = null,
    string? ActorName = null,
    string? ActorRole = null,
    string? LoginName = null,
    string? FailureCode = null,
    string? FailureReason = null,
    IReadOnlyList<FieldChange>? Changes = null);

/// <summary><c>GET /api/v2/dashboard?scope=all|mine</c>. No cross-unit stock total is returned.</summary>
public sealed record DashboardResponse(
    int SkuCount,
    int OpenAlertCount,
    int ActiveCountCount,
    int UnresolvedInvestigationCount,
    string Scope,
    IReadOnlyList<ActivityLogResponse> RecentActivity,
    IReadOnlyList<AlertResponse> Alerts);

public static class AlertTypesV2
{
    public const string LowStock = "lowStock";
    public const string HighStock = "highStock";
    public const string OverCapacity = "overCapacity";
    public const string CountDiscrepancy = "countDiscrepancy";

    public static IReadOnlyList<string> All { get; } = [LowStock, HighStock, OverCapacity, CountDiscrepancy];
}

public static class AlertStatusesV2
{
    public const string Open = "open";
    public const string Resolved = "resolved";

    public static IReadOnlyList<string> All { get; } = [Open, Resolved];
}

public static class DashboardScopes
{
    public const string All = "all";
    public const string Mine = "mine";
}

/// <summary>Object types used in <see cref="SubjectRef.Type"/> (logs, problems).</summary>
public static class SubjectTypes
{
    public const string Product = "product";
    public const string Unit = "unit";
    public const string Category = "category";
    public const string Bin = "bin";
    public const string User = "user";
    public const string Settings = "settings";
    public const string Transaction = "transaction";
    public const string SerializedItem = "serializedItem";
    public const string CountTask = "countTask";
    public const string CountResult = "countResult";
    public const string Alert = "alert";

    public static IReadOnlyList<string> All { get; } =
        [Product, Unit, Category, Bin, User, Settings, Transaction, SerializedItem, CountTask, CountResult, Alert];
}

/// <summary>Activity log types (filter <c>type=</c>).</summary>
public static class ActivityTypes
{
    public const string Login = "login";
    public const string Logout = "logout";
    public const string SwitchRole = "switchRole";
    public const string UserCreate = "userCreate";
    public const string UserUpdate = "userUpdate";
    public const string UserResetPassword = "userResetPassword";
    public const string SettingsUpdate = "settingsUpdate";
    public const string UnitCreate = "unitCreate";
    public const string UnitUpdate = "unitUpdate";
    public const string UnitDelete = "unitDelete";
    public const string CategoryCreate = "categoryCreate";
    public const string CategoryUpdate = "categoryUpdate";
    public const string CategoryDelete = "categoryDelete";
    public const string ProductCreate = "productCreate";
    public const string ProductUpdate = "productUpdate";
    public const string ProductArchive = "productArchive";
    public const string BinCreate = "binCreate";
    public const string BinUpdate = "binUpdate";
    public const string BinArchive = "binArchive";
    public const string RackCreate = "rackCreate";
    public const string Receipt = "receipt";
    public const string Shipment = "shipment";
    public const string Movement = "movement";
    public const string Adjustment = "adjustment";
    public const string CountStart = "countStart";
    public const string CountSubmit = "countSubmit";
    public const string CountConfirm = "countConfirm";
    public const string CountRecount = "countRecount";
    public const string CountCancel = "countCancel";
    public const string CountResolve = "countResolve";
    public const string SerialCorrect = "serialCorrect";

    public static IReadOnlyList<string> All { get; } =
    [
        Login, Logout, SwitchRole, UserCreate, UserUpdate, UserResetPassword, SettingsUpdate,
        UnitCreate, UnitUpdate, UnitDelete, CategoryCreate, CategoryUpdate, CategoryDelete,
        ProductCreate, ProductUpdate, ProductArchive, BinCreate, BinUpdate, BinArchive, RackCreate,
        Receipt, Shipment, Movement, Adjustment,
        CountStart, CountSubmit, CountConfirm, CountRecount, CountCancel, CountResolve, SerialCorrect,
    ];
}
