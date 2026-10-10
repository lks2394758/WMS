namespace Wms.Domain.Identity;

/// <summary>The four fixed roles of release 1 (guide §0.2, §7.3).</summary>
public enum UserRole
{
    SystemAdministrator = 1,
    WarehouseSupervisor = 2,
    ReceivingClerk = 3,
    ShippingClerk = 4,
}
