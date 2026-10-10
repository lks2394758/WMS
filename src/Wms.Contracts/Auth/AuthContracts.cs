namespace Wms.Contracts.Auth;

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    CurrentUserResponse User,
    string TokenType = "Bearer");

public sealed record CurrentUserResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    IReadOnlyList<string> Permissions);

/// <summary>Role names as they appear on the wire.</summary>
public static class Roles
{
    public const string SystemAdministrator = "systemAdministrator";
    public const string WarehouseSupervisor = "warehouseSupervisor";
    public const string ReceivingClerk = "receivingClerk";
    public const string ShippingClerk = "shippingClerk";
}

/// <summary>Named authorization policies (guide §7.2); also listed in <see cref="CurrentUserResponse.Permissions"/>.</summary>
public static class Permissions
{
    public const string CatalogWrite = "Catalog.Write";
    public const string UsersManage = "Users.Manage";
    public const string InventoryReceive = "Inventory.Receive";
    public const string InventoryShip = "Inventory.Ship";
    public const string InventoryCount = "Inventory.Count";
    public const string InventoryAdjust = "Inventory.Adjust";
    public const string AlertsAcknowledge = "Alerts.Acknowledge";
    public const string AuditRead = "Audit.Read";
}
