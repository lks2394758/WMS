using Wms.Application.Common;
using Wms.Contracts.Auth;
using Wms.Domain.Identity;

namespace Wms.Application.Auth;

/// <summary>
/// The fixed role → permission matrix of release 1 (guide §7.2/§7.3). Each permission is also the name of an
/// authorization policy. Reads (catalog, locations, barcodes, balances) only require a signed-in user.
/// </summary>
public static class RolePermissions
{
    public static IReadOnlyList<string> AllPermissions { get; } =
    [
        Permissions.CatalogWrite, Permissions.UsersManage, Permissions.InventoryReceive, Permissions.InventoryShip,
        Permissions.InventoryCount, Permissions.InventoryAdjust, Permissions.AlertsAcknowledge, Permissions.AuditRead,
    ];

    private static readonly Dictionary<UserRole, string[]> Matrix = new()
    {
        [UserRole.SystemAdministrator] = [.. AllPermissions],
        [UserRole.WarehouseSupervisor] =
        [
            Permissions.CatalogWrite, Permissions.InventoryReceive, Permissions.InventoryShip, Permissions.InventoryCount,
            Permissions.InventoryAdjust, Permissions.AlertsAcknowledge, Permissions.AuditRead,
        ],
        [UserRole.ReceivingClerk] = [Permissions.InventoryReceive],
        [UserRole.ShippingClerk] = [Permissions.InventoryShip],
    };

    public static IReadOnlyList<string> For(UserRole role) =>
        Matrix.TryGetValue(role, out var permissions)
            ? permissions
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");

    /// <summary>Wire names of the roles that hold <paramref name="permission"/>.</summary>
    public static IReadOnlyList<string> RolesWith(string permission) =>
        Matrix.Where(entry => entry.Value.Contains(permission, StringComparer.Ordinal))
            .Select(entry => WireNames.Of(entry.Key))
            .ToList();
}
