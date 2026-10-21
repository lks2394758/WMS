using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;
using Wms.Contracts.V2.Inventory;
using Wms.Contracts.V2.Monitoring;

namespace Wms.Api.Tests.V2;

/// <summary>/api/v2/alerts, /activity-logs and /dashboard (B5a) over HTTP in the v2 process mode on temporary files.</summary>
public sealed class V2MonitoringApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-monitoring-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Monitoring_reads_are_signed_in_only_validated_and_read_only()
    {
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();
        foreach (var path in new[] { "/api/v2/alerts", "/api/v2/activity-logs", "/api/v2/dashboard" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        using var admin = await SignedInAsync(factory, "admin");
        var product = (await (await PostAsync(admin, "/api/v2/products", new ProductCreateRequest("Bolt", "1", 0, false, []))).Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id;
        var bin = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options)).Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!.Id;
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(admin, "/api/v2/inventory/receipts", new InventoryOperationRequest([new InventoryLine(product, ToBinId: bin, Quantity: "3", Capacity: "100")]))).StatusCode);

        using var receiver = await SignedInAsync(factory, "receiver");
        var alerts = (await receiver.GetFromJsonAsync<Page<AlertResponse>>($"/api/v2/alerts?status=open&binId={bin}", WmsJson.Options))!;
        var alert = Assert.Single(alerts.Items);
        Assert.Equal((AlertTypesV2.LowStock, "3", "Bolt", product), (alert.Type, alert.Trigger.Quantity, alert.ProductName, alert.ProductId));
        Assert.False(string.IsNullOrEmpty(alert.BinCode));

        var logs = (await receiver.GetFromJsonAsync<Page<ActivityLogResponse>>($"/api/v2/activity-logs?productId={product}&type=receipt&success=true", WmsJson.Options))!;
        Assert.Contains(Assert.Single(logs.Items).Subjects, s => s.Type == SubjectTypes.Bin && s.Id == bin);

        var sameInstant = await receiver.GetAsync("/api/v2/activity-logs?from=2026-10-20T10:00:00Z&to=2026-10-20T11:00:00%2B01:00");
        Assert.Equal(HttpStatusCode.OK, sameInstant.StatusCode);
        Assert.Empty((await sameInstant.Content.ReadFromJsonAsync<Page<ActivityLogResponse>>(WmsJson.Options))!.Items);

        var mine =(await receiver.GetFromJsonAsync<DashboardResponse>("/api/v2/dashboard?scope=mine", WmsJson.Options))!;
        Assert.Equal((1, 1, DashboardScopes.Mine), (mine.SkuCount, mine.OpenAlertCount, mine.Scope));
        Assert.All(mine.RecentActivity, a => Assert.Equal(ActivityTypes.Login, a.Type));

        foreach (var path in new[]
        {
            "/api/v2/alerts?status=closed", "/api/v2/alerts?type=x", "/api/v2/alerts?productId=abc", "/api/v2/alerts?limit=500",
            "/api/v2/activity-logs?from=nope", "/api/v2/activity-logs?from=2026-10-20T10:00:00Z&to=2026-10-20T10:30:00%2B01:00",
            "/api/v2/activity-logs?from=2026-10-21T00:00:00Z&to=2026-10-20T00:00:00Z","/api/v2/activity-logs?type=x", "/api/v2/activity-logs?actorId=-1",
            "/api/v2/dashboard?scope=team",
        })
        {
            await AssertProblemAsync(await receiver.GetAsync(path), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        }

        Assert.Contains(
            (await admin.PostAsJsonAsync("/api/v2/alerts", new { }, WmsJson.Options)).StatusCode,
            new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound });
    }

    private static Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("V2:Enabled", "true")
            .UseSetting("V2:Sqlite:DataSource", Path.Combine(_root, "App_Data", "wms-v2-demo.db"))
            .UseSetting("V2:Seed:Password", _password)
            .UseSetting("Persistence:Sqlite:DataSource", Path.Combine(_root, "App_Data", "wms-demo.db"))
            .ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000))));

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(username, _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    private static async Task<JsonNode> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(code, problem["code"]?.GetValue<string>());
        return problem;
    }
}
