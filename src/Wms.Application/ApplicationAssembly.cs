using System.Reflection;

namespace Wms.Application;

/// <summary>Marker for assembly scanning and architecture tests.</summary>
public static class ApplicationAssembly
{
    public static Assembly Reference => typeof(ApplicationAssembly).Assembly;
}
