using System.Runtime.CompilerServices;

namespace Wms.Api.Tests;

/// <summary>
/// The HTTP tests run on the explicit in-memory test provider unless a test selects SQLite itself
/// (<see cref="SqliteApiTests"/>): the API default is the SQLite demonstration file, which tests must not touch.
/// A test that needs another provider sets it with <c>UseSetting</c>, which takes precedence over environment variables.
/// </summary>
internal static class TestEnvironment
{
#pragma warning disable CA2255 // A module initializer is the only hook that runs before every WebApplicationFactory host.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseInMemoryPersistence() =>
        Environment.SetEnvironmentVariable("Persistence__Provider", "InMemory");
}
