using System.Reflection;

namespace Wms.Persistence.Abstractions;

/// <summary>
/// Marker for architecture tests. Persistence ports (IInventoryTransactionScope, IInventoryLockedStore, ...)
/// and persistence records (audit, outbox, idempotency) arrive in milestone M2 (guide §6.2).
/// </summary>
public static class PersistenceAbstractionsAssembly
{
    public static Assembly Reference => typeof(PersistenceAbstractionsAssembly).Assembly;
}
