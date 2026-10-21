using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Wms.Api.Tests;

/// <summary>
/// Detects drift between the OpenAPI document the API generates at runtime (<c>GET /openapi/v1.json</c>) and the
/// committed contract <c>contracts/openapi/wms.v1.json</c> that client teams build against.
/// <para>
/// Both documents are compared as parsed JSON, so formatting and line endings do not matter. <c>servers</c> is
/// ignored: at runtime it reflects the request host, while the committed file names the intranet host.
/// </para>
/// <para>
/// After an intended API change, regenerate the committed file and review its diff:
/// <c>$env:WMS_UPDATE_OPENAPI = "1"; dotnet test tests/Wms.Api.Tests --filter OpenApiContractTests</c>.
/// </para>
/// </summary>
public sealed class OpenApiContractTests : IDisposable
{
    private const string UpdateVariable = "WMS_UPDATE_OPENAPI";
    private const int MaxReportedDifferences = 20;

    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Committed_openapi_document_matches_the_runtime_document()
    {
        using var client = _factory.CreateClient();
        var runtimeJson = await client.GetStringAsync("/openapi/v1.json");
        var runtime = JsonNode.Parse(runtimeJson)!.AsObject();
        var committedPath = CommittedDocumentPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            WriteCommittedDocument(committedPath, runtime);
        }

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(committedPath))!.AsObject();
        runtime.Remove("servers");
        committed.Remove("servers");

        var differences = new List<string>();
        Compare("$", committed, runtime, differences);

        Assert.True(
            differences.Count == 0,
            $"contracts/openapi/wms.v1.json differs from GET /openapi/v1.json. Review the API change, then regenerate " +
            $"with {UpdateVariable}=1 (see {nameof(OpenApiContractTests)}) and update contracts/changelog.md." +
            Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    [Fact]
    public async Task Only_sign_in_operations_are_public_and_every_other_operation_requires_the_bearer_scheme()
    {
        using var client = _factory.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!.AsObject();

        var scheme = document["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Null(document["security"]); // no document-wide requirement: it would make the public operations protected

        var operations = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(op => (Name: $"{op.Key.ToUpperInvariant()} {path.Key}", Operation: op.Value!)))
            .ToList();
        var publicOperations = operations.Where(op => op.Operation["security"] is null).Select(op => op.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["POST /api/v1/auth/login", "POST /api/v1/auth/refresh"], publicOperations);
        Assert.All(
            operations.Where(op => op.Operation["security"] is not null),
            op => Assert.Equal("""[{"Bearer":[]}]""", op.Operation["security"]!.ToJsonString()));
    }

    /// <summary>Writes the runtime document, keeping the committed <c>servers</c> entry (the intranet host).</summary>
    private static void WriteCommittedDocument(string path, JsonObject runtime)
    {
        var document = runtime.DeepClone().AsObject();
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject previous
            && previous["servers"] is { } servers)
        {
            document["servers"] = servers.DeepClone();
        }

        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json.ReplaceLineEndings("\r\n") + "\r\n");
    }

    private static void Compare(string path, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        if (differences.Count >= MaxReportedDifferences)
        {
            return;
        }

        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var name in e.Select(p => p.Key).Union(a.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    var child = $"{path}.{name}";
                    if (!a.ContainsKey(name))
                    {
                        differences.Add($"{child}: committed only");
                    }
                    else if (!e.ContainsKey(name))
                    {
                        differences.Add($"{child}: runtime only");
                    }
                    else
                    {
                        Compare(child, e[name], a[name], differences);
                    }
                }

                return;
            case (JsonArray e, JsonArray a) when e.Count == a.Count:
                for (var i = 0; i < e.Count; i++)
                {
                    Compare($"{path}[{i}]", e[i], a[i], differences);
                }

                return;
            default:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    differences.Add($"{path}: committed {Describe(expected)}, runtime {Describe(actual)}");
                }

                return;
        }
    }

    private static string Describe(JsonNode? node)
    {
        var json = node?.ToJsonString() ?? "null";
        return json.Length <= 120 ? json : json[..117] + "...";
    }

    private static string CommittedDocumentPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "openapi", "wms.v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("contracts/openapi/wms.v1.json was not found above the test output directory.");
    }
}
