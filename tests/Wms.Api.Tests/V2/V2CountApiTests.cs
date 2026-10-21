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
using Wms.Contracts.V2.Counts;
using Wms.Contracts.V2.Inventory;

namespace Wms.Api.Tests.V2;

/// <summary>/api/v2/counts (B4a) over HTTP in the v2 process mode on temporary files.</summary>
public sealed class V2CountApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-count-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Counts_start_replay_submit_recount_cancel_with_the_contract_codes()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var bin = await StockedBinAsync(admin, "7");

        using var receiver = await SignedInAsync(factory, "receiver");
        await AssertProblemAsync(await StartAsync(receiver, bin, Guid.NewGuid().ToString()), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);
        await AssertProblemAsync(
            await receiver.PostAsJsonAsync("/api/v2/counts", new CountStartRequest(bin), WmsJson.Options), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);

        using var supervisor = await SignedInAsync(factory, "supervisor");
        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync("/api/v2/counts", new CountStartRequest(bin), WmsJson.Options), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        var key = Guid.NewGuid().ToString();
        var started = await StartAsync(supervisor, bin, key);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var raw = await started.Content.ReadAsStringAsync();
        Assert.DoesNotContain("bookQuantity", raw, StringComparison.Ordinal);
        var task = (await started.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;

        var replay = await StartAsync(supervisor, bin, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(ApiHeaders.IdempotentReplayed).Single());
        Assert.Equal(task.Id, (await replay.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!.Id);
        await AssertProblemAsync(await StartAsync(supervisor, bin, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ProblemCodesV2.CountTaskExists);

        var read = (await receiver.GetFromJsonAsync<CountTaskResponse>($"/api/v2/counts/{task.Id}", WmsJson.Options))!;
        Assert.Empty(read.AllowedActions);
        Assert.NotNull(read.CurrentRound);
        var list = (await receiver.GetFromJsonAsync<Page<CountTaskListItem>>($"/api/v2/counts?status=inProgress&binId={bin}", WmsJson.Options))!;
        Assert.Equal(task.Id, Assert.Single(list.Items).Id);

        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/counts/{task.Id}/submit", new CountSubmitRequest("5", 2, 1), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.CountStateConflict);
        var submitted = await supervisor.PostAsJsonAsync($"/api/v2/counts/{task.Id}/submit", new CountSubmitRequest("5", 1, 1), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var pending = (await submitted.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;
        Assert.Equal(("7", "5", "-2"), (pending.Results[0].BookQuantity, pending.Results[0].ActualQuantity, pending.Results[0].Difference));
        Assert.Contains(CountActions.Confirm, pending.AllowedActions);
        Assert.Equal(HttpStatusCode.OK, (await supervisor.PostAsJsonAsync($"/api/v2/counts/{task.Id}/submit", new CountSubmitRequest("5", 1, 1), WmsJson.Options)).StatusCode);
        await AssertProblemAsync(
            await supervisor.PostAsJsonAsync($"/api/v2/counts/{task.Id}/submit", new CountSubmitRequest("6", 1, 1), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.CountRoundAlreadySubmitted);

        var recount = await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/recount", new CountRecountRequest("check", 1, pending.Version, pending.LatestResultId), WmsJson.Options);
        var round2 = (await recount.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;
        Assert.Equal((2, CountStatuses.PendingConfirmation), (round2.Round, round2.StatusBeforeRound));
        await AssertProblemAsync(
            await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/cancel", new CountCancelRequest("x", 2, pending.Version), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.VersionConflict);
        var cancel = await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/cancel", new CountCancelRequest("x", 2, round2.Version), WmsJson.Options);
        Assert.Equal(CountStatuses.PendingConfirmation, (await cancel.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!.Status);

        await AssertProblemAsync(await receiver.GetAsync("/api/v2/counts/999"), HttpStatusCode.NotFound, ProblemCodesV2.NotFound);
    }

    [Fact]
    public async Task Confirm_requires_a_key_replays_with_the_header_and_resolve_follows_the_role()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        using var receiver = await SignedInAsync(factory, "receiver");
        var bin = await StockedBinAsync(admin, "7");
        var task = (await (await StartAsync(admin, bin, Guid.NewGuid().ToString())).Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;
        task = (await (await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/submit", new CountSubmitRequest("5", 1, 1), WmsJson.Options))
            .Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;
        var body = new CountConfirmRequest(task.LatestResultId!, task.Round, task.Version, "short");

        await AssertProblemAsync(
            await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/confirm", body, WmsJson.Options), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        await AssertProblemAsync(await ConfirmAsync(receiver, task.Id, body, Guid.NewGuid().ToString()), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);
        var key = Guid.NewGuid().ToString();
        var first = await ConfirmAsync(admin, task.Id, body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var done = (await first.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!;
        Assert.Equal((CountStatuses.Completed, CountResultDispositions.Adjusted), (done.Status, done.Results[0].Disposition));
        Assert.NotNull(done.Results[0].AdjustmentTransactionId);
        var replay = await ConfirmAsync(admin, task.Id, body, key);
        Assert.Equal("true", replay.Headers.GetValues(ApiHeaders.IdempotentReplayed).Single());
        await AssertProblemAsync(await ConfirmAsync(admin, task.Id, body, Guid.NewGuid().ToString()), HttpStatusCode.Conflict, ProblemCodesV2.VersionConflict);

        await AssertProblemAsync(
            await receiver.PostAsJsonAsync($"/api/v2/counts/{task.Id}/resolve", new CountResolveRequest("c", true, done.Version), WmsJson.Options),
            HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);
        await AssertProblemAsync(
            await admin.PostAsJsonAsync($"/api/v2/counts/{task.Id}/resolve", new CountResolveRequest("c", true, done.Version), WmsJson.Options),
            HttpStatusCode.Conflict, ProblemCodesV2.CountStateConflict);
    }

    [Fact]
    public async Task Parallel_start_replays_with_one_key_create_one_task()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var bin = await StockedBinAsync(admin, "3");
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => StartAsync(admin, bin, key)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<CountTaskResponse>(WmsJson.Options))!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(4, responses.Count(r => r.Headers.Contains(ApiHeaders.IdempotentReplayed)));
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string id, CountConfirmRequest body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/counts/{id}/confirm") { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string bin, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/counts") { Content = JsonContent.Create(new CountStartRequest(bin), options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key);
        return client.SendAsync(request);
    }

    private static async Task<string> StockedBinAsync(HttpClient admin, string quantity)
    {
        var product = await PostAsync(admin, "/api/v2/products", new ProductCreateRequest("Bolt", "1", 0, false, []));
        var productId = (await product.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id;
        var bin = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options))
            .Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!.Id;
        var receipt = await PostAsync(
            admin, "/api/v2/inventory/receipts", new InventoryOperationRequest([new InventoryLine(productId, ToBinId: bin, Quantity: quantity, Capacity: "100")]));
        Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        return bin;
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
