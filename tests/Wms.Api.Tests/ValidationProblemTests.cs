using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Wms.Application.Persistence.InMemory;
using Wms.Contracts.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.Inventory;
using Wms.Domain.Identity;

namespace Wms.Api.Tests;

/// <summary>
/// The validation error contract on the inventory writes: 400 <c>application/problem+json</c> with
/// <c>VALIDATION_FAILED</c>, the catalog type and title, and <c>errors</c> keyed by the request field's JSON path.
/// Covers both the boundary rules and binding failures (unreadable JSON, missing body).
/// </summary>
public sealed class ValidationProblemTests : IDisposable
{
    private const string ValidationType = "https://wms-api.company.intra/problems/validation-failed";

    private static readonly string Location = DevelopmentData.ShelfA0101.Value.ToString();
    private static readonly string Widget = DevelopmentData.Widget.Value.ToString();

    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly WebApplicationFactory<Program> _factory;

    public ValidationProblemTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["DevelopmentSeed:Password"] = _password })));
    }

    public void Dispose() => _factory.Dispose();

    public static TheoryData<string, string, string[]> InvalidBodies => new()
    {
        {
            "receipts",
            $$"""{"locationId":"{{Guid.Empty}}","lines":[{"productId":"{{Widget}}","quantity":0},{"productId":"{{Widget}}","quantity":1}]}""",
            ["locationId", "lines[0].quantity", "lines[1].productId"]
        },
        {
            "movements",
            $$"""{"fromLocationId":"{{Location}}","toLocationId":"{{Location}}","lines":[{"productId":"{{Guid.Empty}}","quantity":-1}]}""",
            ["toLocationId", "lines[0].productId", "lines[0].quantity"]
        },
        {
            "shipments",
            $$"""{"locationId":"{{Location}}","lines":[],"referenceNo":"{{new string('x', 200)}}"}""",
            ["lines", "referenceNo"]
        },
        {
            "counts",
            $$"""{"locationId":"{{Location}}","reason":" ","lines":[{"productId":"{{Widget}}","countedQuantity":-1}]}""",
            ["reason", "lines[0].countedQuantity"]
        },
        {
            "adjustments",
            $$"""{"locationId":"{{Location}}","reason":"Damaged","lines":[{"productId":"{{Widget}}","quantityDelta":0}]}""",
            ["lines[0].quantityDelta"]
        },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_command_reports_every_field_by_its_json_path(string endpoint, string json, string[] fields)
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(client, endpoint, json, Guid.NewGuid().ToString());

        var errors = await AssertValidationProblemAsync(response);
        Assert.Equal(fields.Order(StringComparer.Ordinal), errors.Keys.Order(StringComparer.Ordinal));
        Assert.All(errors.Values, messages => Assert.NotEmpty(messages));
    }

    [Fact]
    public async Task Invalid_body_and_missing_idempotency_key_are_reported_together()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(client, "receipts", $$"""{"locationId":"{{Location}}","lines":[]}""", idempotencyKey: null);

        var errors = await AssertValidationProblemAsync(response);
        Assert.Equal([ApiHeaders.IdempotencyKey, "lines"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Malformed_idempotency_key_is_keyed_by_the_header_name()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(
            client, "receipts", $$"""{"locationId":"{{Location}}","lines":[{"productId":"{{Widget}}","quantity":1}]}""", "not-a-uuid");

        var errors = await AssertValidationProblemAsync(response);
        Assert.Equal([ApiHeaders.IdempotencyKey], errors.Keys);
    }

    [Fact]
    public async Task Unreadable_field_value_is_keyed_by_its_json_path()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(
            client, "shipments", $$"""{"locationId":"{{Location}}","lines":[{"productId":"{{Widget}}","quantity":"many"}]}""",
            Guid.NewGuid().ToString());

        var errors = await AssertValidationProblemAsync(response);
        Assert.Contains("lines[0].quantity", errors.Keys);
        Assert.DoesNotContain(errors.Keys, key => key.StartsWith('$'));
    }

    [Fact]
    public async Task Missing_body_is_keyed_as_body()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(client, "adjustments", json: null, Guid.NewGuid().ToString());

        var errors = await AssertValidationProblemAsync(response);
        Assert.Equal(["body"], errors.Keys);
    }

    [Fact]
    public async Task Rejected_command_changes_no_balance()
    {
        using var client = await SignInAsync(UserRole.WarehouseSupervisor);

        var response = await PostRawAsync(
            client, "receipts", $$"""{"locationId":"{{Location}}","lines":[{"productId":"{{Widget}}","quantity":5},{"productId":"{{Guid.Empty}}","quantity":1}]}""",
            Guid.NewGuid().ToString());

        await AssertValidationProblemAsync(response);
        var balances = await client.GetFromJsonAsync<CursorPage<InventoryBalanceResponse>>(
            $"/api/v1/inventory/balances?productId={Widget}", WmsJson.Options);
        Assert.Empty(balances!.Items);
    }

    private async Task<HttpClient> SignInAsync(UserRole role)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(DevelopmentData.Usernames[role], _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string endpoint, string? json, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/" + endpoint);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
        }

        return client.SendAsync(request);
    }

    private static async Task<IReadOnlyDictionary<string, string[]>> AssertValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<WmsProblemDetails>(WmsJson.Options);
        Assert.NotNull(problem);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.Equal(400, problem.Status);
        Assert.Equal(ValidationType, problem.Type);
        Assert.Equal("Validation failed", problem.Title);
        Assert.False(string.IsNullOrEmpty(problem.TraceId));
        Assert.NotNull(problem.Errors);
        Assert.NotEmpty(problem.Errors);
        return problem.Errors;
    }
}
