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

namespace Wms.Api.Tests.V2;

/// <summary>/api/v2/serialized-items over HTTP in the v2 process mode on temporary files.</summary>
public sealed class V2SerialApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-serial-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Items_are_listed_read_and_corrected_with_the_contract_codes()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var item = await ReceiveAsync(admin, "SC-1");

        using var receiver = await SignedInAsync(factory, "receiver");
        var listed = (await receiver.GetFromJsonAsync<Page<SerializedItemListItem>>("/api/v2/serialized-items?inStock=true&search=sc-", WmsJson.Options))!;
        Assert.Equal(item, Assert.Single(listed.Items).Id);
        await AssertProblemAsync(
            await receiver.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-2", "fix", 1), WmsJson.Options),
            HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);

        using var supervisor = await SignedInAsync(factory, "supervisor");
        var corrected = await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-2", "fix", 1), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var body = (await corrected.Content.ReadFromJsonAsync<SerializedItemResponse>(WmsJson.Options))!;
        Assert.Equal(("SC-2", 2, "SC-1"), (body.SerialNumber, body.Version, Assert.Single(body.HistoricalSerialNumbers)));

        // A stale version conflicts even with the current number (contract §1); only the current version makes it a no-op.
        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-2", "fix", 1), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.VersionConflict);
        var noOp = await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-2", "fix", 2), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        var unchanged = (await noOp.Content.ReadFromJsonAsync<SerializedItemResponse>(WmsJson.Options))!;
        Assert.Equal(("SC-2", 2, "SC-1"), (unchanged.SerialNumber, unchanged.Version, Assert.Single(unchanged.HistoricalSerialNumbers)));
        Assert.Single(unchanged.Corrections);

        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-3", "fix", 1), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.VersionConflict);
        var taken = await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("sc-1", "fix", 2), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.AlreadyExists);
        Assert.NotNull(taken["errors"]?["serialNumber"]);
        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-3", " ", 2), WmsJson.Options),
            HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);

        var byOld = (await receiver.GetFromJsonAsync<Page<SerializedItemListItem>>("/api/v2/serialized-items?search=SC-1", WmsJson.Options))!;
        Assert.Equal(("SC-2", "SC-1"), (Assert.Single(byOld.Items).SerialNumber, byOld.Items[0].MatchedHistoricalSerialNumber));
        var detail = (await receiver.GetFromJsonAsync<SerializedItemResponse>($"/api/v2/serialized-items/{item}", WmsJson.Options))!;
        Assert.Equal(("SC-1", "fix", "warehouseSupervisor"), (Assert.Single(detail.History).SerialNumber, Assert.Single(detail.Corrections).Reason, detail.Corrections[0].ActorRole));
        await AssertProblemAsync(await receiver.GetAsync("/api/v2/serialized-items/999"), HttpStatusCode.NotFound, ProblemCodesV2.NotFound);
    }

    [Fact]
    public async Task The_old_number_is_refused_by_inventory_with_the_current_number()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var item = await ReceiveAsync(admin, "SC-1");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v2/serialized-items/{item}/correct", new SerialCorrectionRequest("SC-2", "fix", 1), WmsJson.Options)).StatusCode);
        var product = (await admin.GetFromJsonAsync<SerializedItemResponse>($"/api/v2/serialized-items/{item}", WmsJson.Options))!.ProductId;
        var bin = (await admin.GetFromJsonAsync<SerializedItemResponse>($"/api/v2/serialized-items/{item}", WmsJson.Options))!.BinId!;

        var old = await AssertProblemAsync(
            await PostAsync(admin, "/api/v2/inventory/shipments", new InventoryOperationRequest([new InventoryLine(product, FromBinId: bin, SerialNumbers: ["SC-1"])])),
            HttpStatusCode.Conflict, ProblemCodesV2.SerialNumberSuperseded);
        Assert.Equal("SC-2", old["subjects"]?[0]?["label"]?.GetValue<string>());

        var shipped = await PostAsync(admin, "/api/v2/inventory/shipments", new InventoryOperationRequest([new InventoryLine(product, FromBinId: bin, SerialNumbers: ["SC-2"])]));
        Assert.Equal(HttpStatusCode.Created, shipped.StatusCode);
        Assert.Equal(item, (await shipped.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!.Lines[0].Items![0].SerializedItemId);
    }

    [Fact]
    public async Task The_v1_host_does_not_expose_v2_serial_routes()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/serialized-items")).StatusCode);
    }

    private async Task<string> ReceiveAsync(HttpClient admin, string number)
    {
        var product = await PostAsync(admin, "/api/v2/products", new ProductCreateRequest("Scanner", "1", 0, true, []));
        var productId = (await product.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id;
        var bin = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options))
            .Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!.Id;
        var receipt = await PostAsync(
            admin, "/api/v2/inventory/receipts", new InventoryOperationRequest([new InventoryLine(productId, ToBinId: bin, Capacity: "10", SerialNumbers: [number])]));
        return (await receipt.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!.Lines[0].Items![0].SerializedItemId;
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
