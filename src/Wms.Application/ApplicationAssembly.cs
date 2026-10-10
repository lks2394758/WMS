using System.Reflection;

namespace Wms.Application;

/// <summary>
/// Marker for assembly scanning and architecture tests. Use cases, ports and DependencyInjection.cs
/// arrive in milestone M2 (guide §6).
/// </summary>
public static class ApplicationAssembly
{
    public static Assembly Reference => typeof(ApplicationAssembly).Assembly;
}
