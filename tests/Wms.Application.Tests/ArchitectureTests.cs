using System.Reflection;
using System.Xml.Linq;
using Wms.Contracts.Common;
using Wms.Domain.Common;
using Wms.Persistence.Abstractions;

namespace Wms.Application.Tests;

/// <summary>Enforces the dependency direction of guide §2.3 at the project-file and compiled-assembly level.</summary>
public class ArchitectureTests
{
    private static readonly string[] DatabaseDriverPrefixes =
        ["Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient", "Npgsql", "MySql", "MySqlConnector", "Pomelo"];

    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { "src/Wms.Domain/Wms.Domain.csproj", [] },
        { "src/Wms.Contracts/Wms.Contracts.csproj", [] },
        { "src/Wms.Persistence.Abstractions/Wms.Persistence.Abstractions.csproj", ["Wms.Domain"] },
        { "src/Wms.Application/Wms.Application.csproj", ["Wms.Domain", "Wms.Contracts", "Wms.Persistence.Abstractions"] },
        { "src/Wms.Api/Wms.Api.csproj", ["Wms.Application", "Wms.Contracts", "Wms.Persistence.Provider"] },
        { "src/Wms.Worker/Wms.Worker.csproj", ["Wms.Application", "Wms.Contracts", "Wms.Persistence.Provider"] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Project_references_follow_the_dependency_direction(string project, string[] allowed)
    {
        var path = Path.Combine(RepositoryRoot.Path, project);
        var references = XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToArray();

        Assert.All(references, r => Assert.Contains(r, allowed));
    }

    [Fact]
    public void Domain_assembly_references_no_other_project_or_package()
    {
        var references = ReferencedNames(typeof(Result).Assembly);

        Assert.DoesNotContain(references, n => n.StartsWith("Wms.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    [Fact]
    public void Contracts_assembly_references_no_other_project()
    {
        Assert.DoesNotContain(ReferencedNames(typeof(ProblemCodes).Assembly), n => n.StartsWith("Wms.", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_references_no_host_or_database_driver()
    {
        var references = ReferencedNames(ApplicationAssembly.Reference);

        Assert.DoesNotContain("Wms.Api", references);
        Assert.DoesNotContain("Wms.Worker", references);
        Assert.DoesNotContain(references, n => n.StartsWith("Wms.Persistence.Provider", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => DatabaseDriverPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void Persistence_abstractions_reference_no_database_driver()
    {
        var references = ReferencedNames(PersistenceAbstractionsAssembly.Reference);

        Assert.DoesNotContain(references, n => DatabaseDriverPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));
    }

    private static string[] ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
}
