namespace Wms.Contracts.V2.Auth;

/// <summary>
/// <c>POST /api/v2/auth/login</c>. <see cref="Role"/> optionally selects the session's starting role; when omitted
/// the session starts in the first role of the account's role list. A new login revokes the account's other session.
/// </summary>
public sealed record LoginRequest(string Username, string Password, string? Role = null);

/// <summary>
/// <see cref="Token"/> is an opaque session credential (the server stores only its hash); send it as
/// <c>Authorization: Bearer</c>. It is checked against the persisted session on every request, so logout,
/// a newer login or an account change ends it immediately. There is no refresh token in v2.
/// </summary>
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, CurrentUser User);

/// <summary>The signed-in account and its persisted session role. Never carries a credential or password hash.</summary>
public sealed record CurrentUser(
    string Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    string CurrentRole,
    IReadOnlyList<string> Permissions);

/// <summary>Returned by <c>GET /api/v2/auth/me</c> and <c>POST /api/v2/auth/switch-role</c>. The token is unchanged.</summary>
public sealed record SessionResponse(CurrentUser User, DateTimeOffset ExpiresAt);

/// <summary><c>POST /api/v2/auth/switch-role</c>: changes the current session's role to another assigned role.</summary>
public sealed record SwitchRoleRequest(string Role);

/// <summary>Fixed role names on the wire (same values as v1).</summary>
public static class RoleNames
{
    public const string SystemAdministrator = "systemAdministrator";
    public const string WarehouseSupervisor = "warehouseSupervisor";
    public const string ReceivingClerk = "receivingClerk";
    public const string ShippingClerk = "shippingClerk";

    public static IReadOnlyList<string> All { get; } =
        [SystemAdministrator, WarehouseSupervisor, ReceivingClerk, ShippingClerk];
}

/// <summary>v2 permission names listed in <see cref="CurrentUser.Permissions"/>; they derive from the current role only.</summary>
public static class PermissionNames
{
    public const string CatalogWrite = "Catalog.Write";
    public const string InventoryReceive = "Inventory.Receive";
    public const string InventoryShip = "Inventory.Ship";
    public const string InventoryMove = "Inventory.Move";
    public const string InventoryAdjust = "Inventory.Adjust";
    public const string InventoryCount = "Inventory.Count";
    public const string SerialNumbersCorrect = "SerialNumbers.Correct";
    public const string UsersManage = "Users.Manage";
    public const string SettingsManage = "Settings.Manage";

    public static IReadOnlyList<string> All { get; } =
    [
        CatalogWrite, InventoryReceive, InventoryShip, InventoryMove, InventoryAdjust, InventoryCount,
        SerialNumbersCorrect, UsersManage, SettingsManage,
    ];
}
