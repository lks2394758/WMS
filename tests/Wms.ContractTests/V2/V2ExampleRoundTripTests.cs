using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;

namespace Wms.ContractTests.V2;

/// <summary>
/// Every API v2 request/response type has a JSON example in contracts/examples/v2. Each example must deserialize
/// strictly (no unknown or missing members) and serialize back to the same JSON.
/// </summary>
public class V2ExampleRoundTripTests
{
    private static readonly Dictionary<string, Type> Examples = new()
    {
        ["auth-login-request.json"] = typeof(LoginRequest),
        ["auth-login-with-role-request.json"] = typeof(LoginRequest),
        ["auth-login-response.json"] = typeof(LoginResponse),
        ["auth-session-response.json"] = typeof(SessionResponse),
        ["auth-switch-role-request.json"] = typeof(SwitchRoleRequest),
        ["problem-session-revoked.json"] = typeof(ApiProblem),
        ["problem-role-not-assigned.json"] = typeof(ApiProblem),
        ["problem-bin-frozen.json"] = typeof(ApiProblem),
        ["common-delete-request.json"] = typeof(DeleteRequest),

        ["units-page.json"] = typeof(Page<UnitResponse>),
        ["unit-write-request.json"] = typeof(UnitWriteRequest),
        ["categories-page.json"] = typeof(Page<CategoryResponse>),
        ["category-write-request.json"] = typeof(CategoryWriteRequest),
        ["products-page.json"] = typeof(Page<ProductListItem>),
        ["product-response.json"] = typeof(ProductResponse),
        ["product-create-request.json"] = typeof(ProductCreateRequest),
        ["product-update-request.json"] = typeof(ProductUpdateRequest),

        ["bins-page.json"] = typeof(Page<BinResponse>),
        ["bin-write-request.json"] = typeof(BinWriteRequest),
        ["rack-create-request.json"] = typeof(RackCreateRequest),
        ["rack-create-response.json"] = typeof(RackCreateResponse),

        ["users-page.json"] = typeof(Page<UserResponse>),
        ["user-create-request.json"] = typeof(UserCreateRequest),
        ["user-update-request.json"] = typeof(UserUpdateRequest),
        ["user-reset-password-request.json"] = typeof(ResetPasswordRequest),
        ["settings-response.json"] = typeof(SettingsResponse),
        ["settings-update-request.json"] = typeof(SettingsUpdateRequest),

        ["inventory-receipt-request.json"] = typeof(InventoryOperationRequest),
        ["inventory-shipment-request.json"] = typeof(InventoryOperationRequest),
        ["inventory-movement-request.json"] = typeof(InventoryOperationRequest),
        ["inventory-adjustment-request.json"] = typeof(InventoryOperationRequest),
        ["inventory-transaction-response.json"] = typeof(InventoryTransactionResponse),
        ["inventory-transactions-page.json"] = typeof(Page<TransactionListItem>),
        ["serialized-items-page.json"] = typeof(Page<SerializedItemListItem>),
        ["serialized-item-response.json"] = typeof(SerializedItemResponse),
        ["serial-correction-request.json"] = typeof(SerialCorrectionRequest),

        ["count-start-request.json"] = typeof(CountStartRequest),
        ["count-task-recount-in-progress-response.json"] = typeof(CountTaskResponse),
        ["counts-page.json"] = typeof(Page<CountTaskListItem>),
        ["count-submit-request.json"] = typeof(CountSubmitRequest),
        ["count-confirm-request.json"] = typeof(CountConfirmRequest),
        ["count-recount-request.json"] = typeof(CountRecountRequest),
        ["count-cancel-request.json"] = typeof(CountCancelRequest),
        ["count-resolve-request.json"] = typeof(CountResolveRequest),
        ["count-resolution-response.json"] = typeof(CountResolutionResponse),

        ["alerts-page.json"] = typeof(Page<AlertResponse>),
        ["activity-logs-page.json"] = typeof(Page<ActivityLogResponse>),
        ["dashboard-response.json"] = typeof(DashboardResponse),
    };

    /// <summary>String members holding an exact quantity (responses must use the canonical form).</summary>
    private static readonly HashSet<string> QuantityMembers =
    [
        "quantity", "totalQuantity", "capacity", "bookQuantity", "actualQuantity", "difference",
        "fromBefore", "fromAfter", "toBefore", "toAfter", "packagingQuantity", "conversionFactor", "occupancyPercent",
    ];

    /// <summary>String members holding an alert threshold percentage.</summary>
    private static readonly HashSet<string> ThresholdMembers =
        ["lowThreshold", "highThreshold", "effectiveLowThreshold", "effectiveHighThreshold"];

    private static readonly string ExampleFolder = Path.Combine(RepositoryRoot.Path, "contracts", "examples", "v2");

    public static TheoryData<string> ExampleFiles => new(Examples.Keys);

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Example_deserializes_strictly_and_round_trips(string file)
    {
        var json = File.ReadAllText(Path.Combine(ExampleFolder, file));
        var original = JsonNode.Parse(json);

        var dto = JsonSerializer.Deserialize(json, Examples[file], V2Json.Strict);
        var roundTripped = JsonNode.Parse(JsonSerializer.Serialize(dto, Examples[file], WmsJson.Options));

        Assert.NotNull(dto);
        Assert.True(
            JsonNode.DeepEquals(original, roundTripped),
            $"{file} changed on round trip:\n{roundTripped?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Example_ids_quantities_and_thresholds_use_the_wire_formats(string file)
    {
        var isResponse = !Examples[file].Name.EndsWith("Request", StringComparison.Ordinal);
        var problems = new List<string>();
        Walk(JsonNode.Parse(File.ReadAllText(Path.Combine(ExampleFolder, file)))!, "$", isResponse, problems);

        Assert.Empty(problems);
    }

    [Fact]
    public void Every_v2_example_file_is_mapped_to_a_contract()
    {
        var files = Directory.GetFiles(ExampleFolder, "*.json").Select(Path.GetFileName);

        Assert.All(files, f => Assert.True(Examples.ContainsKey(f!), $"{f} has no v2 contract type mapping."));
    }

    [Fact]
    public void Every_top_level_v2_contract_has_an_example()
    {
        var covered = Examples.Values
            .SelectMany(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Page<>)
                ? new[] { t, t.GetGenericArguments()[0] }
                : new[] { t })
            .ToHashSet();
        var contracts = V2Json.V2Types()
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal)
                     || t.Name.EndsWith("Response", StringComparison.Ordinal)
                     || t.Name.EndsWith("ListItem", StringComparison.Ordinal)
                     || t == typeof(ApiProblem));

        Assert.All(contracts, t => Assert.Contains(t, covered));
    }

    [Fact]
    public void Auth_examples_use_only_fixed_roles_and_known_permissions()
    {
        foreach (var file in new[] { "auth-login-response.json", "auth-session-response.json" })
        {
            var user = JsonNode.Parse(File.ReadAllText(Path.Combine(ExampleFolder, file)))!["user"]!;
            var roles = user["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();

            Assert.NotEmpty(roles);
            Assert.Equal(roles.Count, roles.Distinct().Count());
            Assert.All(roles, r => Assert.Contains(r, RoleNames.All));
            Assert.Contains(user["currentRole"]!.GetValue<string>(), roles);
            Assert.All(user["permissions"]!.AsArray(), p => Assert.Contains(p!.GetValue<string>(), PermissionNames.All));
        }

        var requested = JsonNode.Parse(File.ReadAllText(Path.Combine(ExampleFolder, "auth-switch-role-request.json")))!["role"]!;
        Assert.Contains(requested.GetValue<string>(), RoleNames.All);
    }

    [Fact]
    public void Example_enumerations_use_the_published_values()
    {
        var counts = Load<CountTaskResponse>("count-task-recount-in-progress-response.json");
        Assert.Contains(counts.Status, CountStatuses.All);
        Assert.All(counts.AllowedActions, a => Assert.Contains(a, CountActions.All));
        Assert.All(counts.Results, r => Assert.Contains(r.Disposition, CountResultDispositions.All));

        var bins = Load<Page<BinResponse>>("bins-page.json");
        Assert.All(bins.Items, b => Assert.Contains(b.CapacityStatus, CapacityStatuses.All));

        var alerts = Load<Page<AlertResponse>>("alerts-page.json");
        Assert.All(alerts.Items, a => Assert.Contains(a.Type, AlertTypesV2.All));
        Assert.All(alerts.Items, a => Assert.Contains(a.Status, AlertStatusesV2.All));

        var logs = Load<Page<ActivityLogResponse>>("activity-logs-page.json");
        Assert.All(logs.Items, l => Assert.Contains(l.Type, ActivityTypes.All));
        Assert.All(logs.Items.SelectMany(l => l.Subjects), s => Assert.Contains(s.Type, SubjectTypes.All));

        var transactions = Load<Page<TransactionListItem>>("inventory-transactions-page.json");
        Assert.All(transactions.Items, t => Assert.Contains(t.Type, TransactionTypesV2.All));
    }

    [Fact]
    public void In_progress_count_example_hides_the_current_round_book_quantity()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(ExampleFolder, "count-task-recount-in-progress-response.json")))!;
        var current = node["currentRound"]!.AsObject();

        Assert.DoesNotContain(current, p => p.Key is "bookQuantity" or "difference" or "actualQuantity");
        Assert.All(node["results"]!.AsArray(), r => Assert.True(r!["round"]!.GetValue<int>() < current["round"]!.GetValue<int>()));
    }

    [Fact]
    public void Login_response_times_are_written_as_utc_with_z_suffix()
    {
        var user = new CurrentUser("1", "a", "A", [RoleNames.ShippingClerk], RoleNames.ShippingClerk, [PermissionNames.InventoryShip]);
        var response = new LoginResponse("<t>", new DateTimeOffset(2026, 10, 19, 19, 0, 0, TimeSpan.FromHours(-7)), user);

        var json = JsonSerializer.Serialize(response, WmsJson.Options);

        Assert.Contains("\"expiresAt\":\"2026-10-20T02:00:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_json_ids_are_rejected()
    {
        const string json = """
            {"id":7,"username":"a","displayName":"A","roles":["shippingClerk"],"currentRole":"shippingClerk","permissions":[]}
            """;

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<CurrentUser>(json, V2Json.Strict));
    }

    [Fact]
    public void Numeric_json_quantities_are_rejected()
    {
        const string json = """{"lines":[{"productId":"12","fromBinId":"31","quantity":20.5}]}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<InventoryOperationRequest>(json, V2Json.Strict));
    }

    [Fact]
    public void Missing_required_member_is_rejected()
    {
        const string json = """{"token":"<t>","expiresAt":"2026-10-20T02:00:00Z"}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<LoginResponse>(json, V2Json.Strict));
    }

    [Fact]
    public void Count_commands_require_round_and_version()
    {
        const string json = """{"actualQuantity":"2"}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<CountSubmitRequest>(json, V2Json.Strict));
    }

    private static T Load<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(ExampleFolder, file)), V2Json.Strict)!;

    private static void Walk(JsonNode node, string path, bool isResponse, List<string> problems)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    var at = $"{path}.{key}";
                    if (value is JsonValue && value.GetValueKind() == JsonValueKind.String)
                    {
                        Check(key, value.GetValue<string>(), at, isResponse, problems);
                    }
                    else if (key.EndsWith("Ids", StringComparison.Ordinal) && value is JsonArray ids)
                    {
                        foreach (var id in ids)
                        {
                            if (id is not JsonValue v || v.GetValueKind() != JsonValueKind.String || !WireId.IsValid(v.GetValue<string>()))
                            {
                                problems.Add($"{at}: '{id}' is not a v2 ID string");
                            }
                        }
                    }
                    else
                    {
                        Walk(value, at, isResponse, problems);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item)
                    {
                        Walk(item, $"{path}[{i}]", isResponse, problems);
                    }
                }

                break;
        }
    }

    private static void Check(string key, string text, string at, bool isResponse, List<string> problems)
    {
        if ((key == "id" || key.EndsWith("Id", StringComparison.Ordinal)) && key != "traceId" && !WireId.IsValid(text))
        {
            problems.Add($"{at}: '{text}' is not a v2 ID");
        }
        else if (ThresholdMembers.Contains(key) && !WirePercent.TryParse(text, out _))
        {
            problems.Add($"{at}: '{text}' is not a threshold percentage");
        }
        else if (QuantityMembers.Contains(key))
        {
            var valid = isResponse
                ? WireQuantity.IsCanonical(text)
                : WireQuantity.TryParse(text, QuantitySign.NonZeroSigned, out _) || WireQuantity.TryParse(text, QuantitySign.NonNegative, out _);
            if (!valid)
            {
                problems.Add($"{at}: '{text}' is not a {(isResponse ? "canonical " : string.Empty)}quantity");
            }
        }
    }
}

/// <summary>Shared helpers for the v2 contract tests.</summary>
internal static class V2Json
{
    public static JsonSerializerOptions Strict { get; } = CreateStrict();

    public static IEnumerable<Type> V2Types() =>
        typeof(WmsJson).Assembly.GetExportedTypes()
            .Where(t => t.Namespace!.StartsWith("Wms.Contracts.V2", StringComparison.Ordinal));

    public static IEnumerable<PropertyInfo> V2Properties() =>
        V2Types()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == t && p.Name != "EqualityContract"));

    private static JsonSerializerOptions CreateStrict()
    {
        var options = WmsJson.Apply(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        return options;
    }
}
