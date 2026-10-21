using Wms.Contracts.Auth;
using Wms.Contracts.Inventory;
using Wms.Domain.Identity;
using Wms.Domain.Inventory;

namespace Wms.Application.Common;

/// <summary>Maps domain enums to the wire values of docs/api-conventions.md.</summary>
public static class WireNames
{
    public static string Of(InventoryTransactionType type) => type switch
    {
        InventoryTransactionType.Receipt => TransactionTypes.Receipt,
        InventoryTransactionType.Movement => TransactionTypes.Movement,
        InventoryTransactionType.Shipment => TransactionTypes.Shipment,
        InventoryTransactionType.Count => TransactionTypes.Count,
        InventoryTransactionType.Adjustment => TransactionTypes.Adjustment,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown transaction type."),
    };

    public static string Of(UserRole role) => role switch
    {
        UserRole.SystemAdministrator => Roles.SystemAdministrator,
        UserRole.WarehouseSupervisor => Roles.WarehouseSupervisor,
        UserRole.ReceivingClerk => Roles.ReceivingClerk,
        UserRole.ShippingClerk => Roles.ShippingClerk,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };

    public static bool TryParseRole(string? value, out UserRole role)
    {
        foreach (var candidate in Enum.GetValues<UserRole>())
        {
            if (string.Equals(Of(candidate), value, StringComparison.Ordinal))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }
}
