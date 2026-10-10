namespace Wms.Application.Tests;

internal static class RepositoryRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Wms.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Wms.sln above the test output folder.");
    }
}
