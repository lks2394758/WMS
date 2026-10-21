using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wms.Contracts.Alerts;
using Wms.Contracts.Auth;
using Wms.Contracts.Catalog;
using Wms.Contracts.Common;
using Wms.Contracts.Events;
using Wms.Contracts.Inventory;

namespace Wms.ContractTests;

/// <summary>
/// Every request/response type has a JSON example in contracts/examples (guide §4.4). Each example must
/// deserialize strictly (no unknown or missing members) and serialize back to the same JSON.
/// </summary>
public class ExampleRoundTripTests
{
    private static readonly Dictionary<string, Type> Examples = new()
    {
        ["login-request.json"] = typeof(LoginRequest),
        ["refresh-request.json"] = typeof(RefreshRequest),
        ["login-response.json"] = typeof(LoginResponse),
        ["current-user-response.json"] = typeof(CurrentUserResponse),
        ["product-response.json"] = typeof(ProductResponse),
        ["location-response.json"] = typeof(LocationResponse),
        ["barcode-resolution-response.json"] = typeof(BarcodeResolutionResponse),
        ["receipt-request.json"] = typeof(ReceiptRequest),
        ["movement-request.json"] = typeof(MovementRequest),
        ["shipment-request.json"] = typeof(ShipmentRequest),
        ["count-request.json"] = typeof(CountRequest),
        ["adjustment-request.json"] = typeof(AdjustmentRequest),
        ["shipment-response.json"] = typeof(InventoryTransactionResponse),
        ["inventory-balance-response.json"] = typeof(InventoryBalanceResponse),
        ["alert-response.json"] = typeof(AlertResponse),
        ["problem-details.json"] = typeof(WmsProblemDetails),
        ["inventory-changed-event.json"] = typeof(InventoryChangedV1),
    };

    private static readonly JsonSerializerOptions Strict = CreateStrict();

    public static TheoryData<string> ExampleFiles => new(Examples.Keys);

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Example_deserializes_strictly_and_round_trips(string file)
    {
        var json = File.ReadAllText(Path.Combine(RepositoryRoot.Path, "contracts", "examples", file));
        var original = JsonNode.Parse(json);

        var dto = JsonSerializer.Deserialize(json, Examples[file], Strict);
        var roundTripped = JsonNode.Parse(JsonSerializer.Serialize(dto, Examples[file], WmsJson.Options));

        Assert.NotNull(dto);
        Assert.True(
            JsonNode.DeepEquals(original, roundTripped),
            $"{file} changed on round trip:\n{roundTripped?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    [Fact]
    public void Every_example_file_is_mapped_to_a_contract()
    {
        var files = Directory.GetFiles(Path.Combine(RepositoryRoot.Path, "contracts", "examples"), "*.json")
            .Select(Path.GetFileName);

        Assert.All(files, f => Assert.True(Examples.ContainsKey(f!), $"{f} has no contract type mapping."));
    }

    [Fact]
    public void Every_top_level_contract_has_an_example()
    {
        var contracts = typeof(WmsJson).Assembly.GetExportedTypes()
            .Where(t => !t.Namespace!.StartsWith("Wms.Contracts.V2", StringComparison.Ordinal))
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal)
                     || t.Name.EndsWith("Response", StringComparison.Ordinal)
                     || t.Name.EndsWith("V1", StringComparison.Ordinal)
                     || t == typeof(WmsProblemDetails));

        Assert.All(contracts, t => Assert.Contains(t, Examples.Values));
    }

    [Fact]
    public void Times_are_written_as_utc_with_z_suffix()
    {
        var balance = new InventoryBalanceResponse(
            Guid.Empty, Guid.Empty, Guid.Empty, 1, new DateTimeOffset(2026, 10, 10, 11, 0, 0, TimeSpan.FromHours(-7)));

        var json = JsonSerializer.Serialize(balance, WmsJson.Options);

        Assert.Contains("\"updatedAt\":\"2026-10-10T18:00:00Z\"", json, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateStrict()
    {
        var options = WmsJson.Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        return options;
    }
}
