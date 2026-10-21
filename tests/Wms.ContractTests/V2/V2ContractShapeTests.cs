using System.Text.RegularExpressions;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Common;

namespace Wms.ContractTests.V2;

public partial class V2ContractShapeTests
{
    private static readonly HashSet<Type> ForbiddenTypes =
        [typeof(Guid), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)];

    [Fact]
    public void Identifiers_are_strings()
    {
        var offenders = V2Json.V2Properties()
            .Where(p => p.Name.EndsWith("Id", StringComparison.Ordinal) && p.PropertyType != typeof(string))
            .Concat(V2Json.V2Properties()
                .Where(p => p.Name.EndsWith("Ids", StringComparison.Ordinal) && p.PropertyType != typeof(IReadOnlyList<string>)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}: {p.PropertyType}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_guid_wide_integer_or_floating_point_members()
    {
        var offenders = V2Json.V2Properties()
            .Where(p => ForbiddenTypes.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}: {p.PropertyType}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Responses_never_carry_credentials_except_the_login_token()
    {
        var offenders = V2Json.V2Properties()
            .Where(p => !p.DeclaringType!.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(p => !(p.DeclaringType == typeof(LoginResponse) && p.Name == nameof(LoginResponse.Token)))
            .Where(p => Credential().IsMatch(p.Name))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Quantities_thresholds_and_percentages_are_strings()
    {
        var offenders = V2Json.V2Properties()
            .Where(p => QuantityName().IsMatch(p.Name) && p.PropertyType != typeof(string))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}: {p.PropertyType}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Versions_are_one_wire_type()
    {
        var offenders = V2Json.V2Properties()
            .Where(p => p.Name == "Version" && p.PropertyType != typeof(int) && p.PropertyType != typeof(int?))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}: {p.PropertyType}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Current_count_round_has_no_book_actual_or_difference_member()
    {
        var members = typeof(Wms.Contracts.V2.Counts.CountRoundInfo).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(members, n => n.Contains("Quantity", StringComparison.Ordinal) || n.Contains("Difference", StringComparison.Ordinal));
    }

    [Fact]
    public void Product_list_rows_never_inline_images()
    {
        var members = typeof(Wms.Contracts.V2.Catalog.ProductListItem).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(members, n => n.StartsWith("Image", StringComparison.Ordinal));
    }

    [Fact]
    public void Count_commands_carry_round_and_version()
    {
        Type[] commands =
        [
            typeof(Wms.Contracts.V2.Counts.CountSubmitRequest), typeof(Wms.Contracts.V2.Counts.CountConfirmRequest),
            typeof(Wms.Contracts.V2.Counts.CountRecountRequest), typeof(Wms.Contracts.V2.Counts.CountCancelRequest),
        ];

        Assert.All(commands, t =>
        {
            Assert.Equal(typeof(int), t.GetProperty("Round")!.PropertyType);
            Assert.Equal(typeof(int), t.GetProperty("Version")!.PropertyType);
        });
        Assert.Equal(typeof(string), typeof(Wms.Contracts.V2.Counts.CountConfirmRequest).GetProperty("ResultId")!.PropertyType);
    }

    [Fact]
    public void Wire_value_lists_are_unique()
    {
        IReadOnlyList<string>[] lists =
        [
            Wms.Contracts.V2.Bins.CapacityStatuses.All, Wms.Contracts.V2.Inventory.TransactionTypesV2.All,
            Wms.Contracts.V2.Counts.CountStatuses.All, Wms.Contracts.V2.Counts.CountActions.All,
            Wms.Contracts.V2.Counts.CountResultDispositions.All, Wms.Contracts.V2.Monitoring.AlertTypesV2.All,
            Wms.Contracts.V2.Monitoring.AlertStatusesV2.All, Wms.Contracts.V2.Monitoring.SubjectTypes.All,
            Wms.Contracts.V2.Monitoring.ActivityTypes.All, Wms.Contracts.V2.Catalog.ImageContentTypes.All,
        ];

        Assert.All(lists, l => Assert.Equal(l.Count, l.Distinct().Count()));
    }

    [Fact]
    public void Role_and_permission_lists_are_unique()
    {
        Assert.Equal(4, RoleNames.All.Distinct().Count());
        Assert.Equal(PermissionNames.All.Count, PermissionNames.All.Distinct().Count());
    }

    [Fact]
    public void Problem_codes_are_unique_upper_snake_case_with_a_status()
    {
        var constants = typeof(ProblemCodesV2).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(constants.Order(), ProblemCodesV2.HttpStatus.Keys.Order());
        Assert.All(constants, c => Assert.Matches(UpperSnake(), c));
        Assert.All(ProblemCodesV2.HttpStatus.Values, s => Assert.InRange(s, 400, 599));
    }

    [GeneratedRegex("token|password|hash|digest|secret|credential", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();

    [GeneratedRegex("(Quantity|Capacity|Difference|Before|After|Threshold|Percent|Factor)$")]
    private static partial Regex QuantityName();

    [GeneratedRegex("^[A-Z]+(_[A-Z]+)*$")]
    private static partial Regex UpperSnake();
}
