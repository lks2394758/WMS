using System.Reflection;

namespace Wms.Persistence.Abstractions;

/// <summary>
/// Marker for architecture tests. The persistence ports (guide §6.2) live in this assembly. Implementations: the
/// in-memory provider in <c>Wms.Application</c> (tests) and the SQLite demonstration provider
/// <c>Wms.Persistence.Sqlite</c>. The production provider follows at M4.
/// </summary>
public static class PersistenceAbstractionsAssembly
{
    public static Assembly Reference => typeof(PersistenceAbstractionsAssembly).Assembly;
}
