using System.Reflection;
using System.Text.RegularExpressions;
using Wms.Contracts.Common;
using Wms.Contracts.Events;

namespace Wms.ContractTests;

public partial class ContractShapeTests
{
    private static readonly HashSet<Type> Primitives =
        [typeof(string), typeof(Guid), typeof(int), typeof(bool), typeof(DateTimeOffset)];

    [Fact]
    public void Contracts_expose_only_transport_types()
    {
        var offenders = typeof(WmsJson).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace!.StartsWith("Wms.Contracts", StringComparison.Ordinal))
            .Where(t => !typeof(System.Text.Json.Serialization.JsonConverter).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == t && p.Name != "EqualityContract")
                .Where(p => !IsTransportType(p.PropertyType))
                .Select(p => $"{t.Name}.{p.Name}: {p.PropertyType}"));

        Assert.Empty(offenders);
    }

    [Fact]
    public void Problem_codes_include_every_code_from_the_guide()
    {
        string[] guide =
        [
            "VALIDATION_FAILED", "UNAUTHENTICATED", "FORBIDDEN", "PRODUCT_NOT_FOUND", "LOCATION_NOT_FOUND",
            "PRODUCT_INACTIVE", "BARCODE_AMBIGUOUS", "INSUFFICIENT_STOCK", "CONCURRENCY_CONFLICT",
            "IDEMPOTENCY_CONFLICT", "INTERNAL_ERROR",
        ];

        Assert.All(guide, code => Assert.Contains(code, ProblemCodes.All));
    }

    [Fact]
    public void Problem_codes_are_unique_upper_snake_case()
    {
        Assert.Equal(ProblemCodes.All.Count, ProblemCodes.All.Distinct().Count());
        Assert.All(ProblemCodes.All, code => Assert.Matches(UpperSnake(), code));
    }

    [Fact]
    public void Constants_match_the_all_list()
    {
        var constants = typeof(ProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(constants.Order(), ProblemCodes.All.Order());
    }

    [Fact]
    public void Inventory_event_name_is_versioned()
    {
        Assert.Equal("inventory.changed.v1", InventoryChangedV1.Name);
    }

    private static bool IsTransportType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (Primitives.Contains(type) || type.Assembly == typeof(WmsJson).Assembly)
        {
            return true;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>))
            {
                return IsTransportType(type.GetGenericArguments()[0]);
            }

            if (definition == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                return args[0] == typeof(string) && (args[1] == typeof(string[]) || IsTransportType(args[1]));
            }
        }

        return false;
    }

    [GeneratedRegex("^[A-Z]+(_[A-Z]+)*$")]
    private static partial Regex UpperSnake();
}
