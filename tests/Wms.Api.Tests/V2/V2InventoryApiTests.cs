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

/// <summary>/api/v2/inventory over HTTP in the v2 process mode on temporary files.</summary>
public sealed class V2InventoryApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-inventory-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Receipts_need_a_key_replay_with_the_header_and_appear_in_the_history()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var (product, bin) = await ProductAndBinAsync(admin);
        var body = new InventoryOperationRequest([new InventoryLine(product, ToBinId: bin, Quantity: "2.5", Capacity: "10")], Source: "Supplier");

        var missing = await admin.PostAsJsonAsync("/api/v2/inventory/receipts", body, WmsJson.Options);
        var problem = await AssertProblemAsync(missing, HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        Assert.NotNull(problem["errors"]?["Idempotency-Key"]);

        var key = Guid.NewGuid();
        var first = await PostAsync(admin, "receipts", body, key);
        var retry = await PostAsync(admin, "receipts", body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains(ApiHeaders.IdempotentReplayed));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(ApiHeaders.IdempotentReplayed)));
        var tx = (await first.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!;
        Assert.Equal(tx.Id, (await retry.Content.ReadFromJsonAsync<InventoryTransactionResponse>(WmsJson.Options))!.Id);
        Assert.Equal(("2.5", "0", "2.5"), (tx.Lines[0].Quantity, tx.Lines[0].ToBefore, tx.Lines[0].ToAfter));

        await AssertProblemAsync(
            await PostAsync(admin, "receipts", body with { Source = "Other" }, key), HttpStatusCode.Conflict, ProblemCodesV2.IdempotencyConflict);

        var detail = (await admin.GetFromJsonAsync<InventoryTransactionResponse>($"/api/v2/inventory/transactions/{tx.Id}", WmsJson.Options))!;
        Assert.Equal(tx.Number, detail.Number);
        var list = (await admin.GetFromJsonAsync<Page<TransactionListItem>>("/api/v2/inventory/transactions?type=receipt", WmsJson.Options))!;
        Assert.Equal(tx.Id, Assert.Single(list.Items).Id);
        await AssertProblemAsync(await admin.GetAsync("/api/v2/inventory/transactions/999"), HttpStatusCode.NotFound, ProblemCodesV2.NotFound);
    }

    [Fact]
    public async Task Each_operation_answers_its_contract_codes_and_permissions()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var (product, bin) = await ProductAndBinAsync(admin);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(admin, "receipts", Body(new InventoryLine(product, ToBinId: bin, Quantity: "5", Capacity: "10")), Guid.NewGuid())).StatusCode);

        using var receiver = await SignedInAsync(factory, "receiver");
        await AssertProblemAsync(
            await PostAsync(receiver, "shipments", Body(new InventoryLine(product, FromBinId: bin, Quantity: "1")), Guid.NewGuid()),
            HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);

        using var shipper = await SignedInAsync(factory, "shipper");
        var shortage = await AssertProblemAsync(
            await PostAsync(shipper, "shipments", Body(new InventoryLine(product, FromBinId: bin, Quantity: "6")), Guid.NewGuid()),
            HttpStatusCode.Conflict, ProblemCodesV2.InsufficientStock);
        Assert.NotNull(shortage["errors"]?["lines[0].quantity"]);
        Assert.Contains(shortage["subjects"]!.AsArray(), s => s!["type"]!.GetValue<string>() == "bin");

        await AssertProblemAsync(
            await PostAsync(admin, "receipts", Body(new InventoryLine(product, ToBinId: bin, Quantity: "1.55")), Guid.NewGuid()), // product has 1 decimal
            HttpStatusCode.UnprocessableEntity, ProblemCodesV2.PrecisionExceeded);
        await AssertProblemAsync(
            await PostAsync(admin, "shipments", Body(new InventoryLine(product, FromBinId: bin, Quantity: "1", Capacity: "3")), Guid.NewGuid()),
            HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        await AssertProblemAsync(
            await PostAsync(admin, "adjustments", Body(new InventoryLine(product, ToBinId: bin, Quantity: "-1")), Guid.NewGuid()),
            HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed); // reason required

        var adjusted = await PostAsync(
            admin, "adjustments", new InventoryOperationRequest([new InventoryLine(product, ToBinId: bin, Quantity: "-1")], Reason: "damaged"), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);
        var json = await adjusted.Content.ReadAsStringAsync();
        Assert.Contains("\"quantity\":\"-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"toAfter\":\"4\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frozen_bin_names_its_count_task_and_too_many_serial_items_are_refused_without_side_effects()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var (product, bin) = await ProductAndBinAsync(admin);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(admin, "receipts", Body(new InventoryLine(product, ToBinId: bin, Quantity: "5", Capacity: "10")), Guid.NewGuid())).StatusCode);
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound) " +
            $"VALUES ('CT-000001', {bin}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'pendingConfirmation', 1)");
        Execute($"UPDATE Bins SET FrozenCountTaskId = (SELECT Id FROM CountTasks) WHERE Id = {bin}");
        var task = Scalar("SELECT Id FROM CountTasks");

        var frozen = await AssertProblemAsync(
            await PostAsync(admin, "shipments", Body(new InventoryLine(product, FromBinId: bin, Quantity: "1")), Guid.NewGuid()),
            HttpStatusCode.Conflict, ProblemCodesV2.BinFrozen);
        var subjects = frozen["subjects"]!.AsArray().Select(s => $"{s!["type"]!.GetValue<string>()}:{s["id"]!.GetValue<string>()}").ToList();
        Assert.Contains($"countTask:{task}", subjects);
        Assert.Contains($"bin:{bin}", subjects);

        var scanner = await PostAsync(admin, "/api/v2/products", new ProductCreateRequest("Scanner", "1", 0, true, []), Guid.NewGuid(), absolute: true);
        var scannerId = (await scanner.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id;
        var other = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("B", "R1", "01", "01"), WmsJson.Options))
            .Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!.Id;
        var transactions = Scalar("SELECT COUNT(*) FROM InventoryTransactions");
        var tooMany = new InventoryOperationRequest(
        [
            new InventoryLine(scannerId, ToBinId: other, Capacity: "10", GenerateSerialCount: 5_000),
            new InventoryLine(scannerId, ToBinId: other, SerialNumbers: Enumerable.Range(1, 5_001).Select(i => $"X-{i}").ToList()),
        ]);

        await AssertProblemAsync(await PostAsync(admin, "receipts", tooMany, Guid.NewGuid()), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM SerializedItems"));
        Assert.Equal(transactions, Scalar("SELECT COUNT(*) FROM InventoryTransactions"));
        Assert.Equal(0L, Scalar("SELECT LastSerialNumber FROM SystemSettings"));
    }

    [Fact]
    public async Task The_v1_host_does_not_expose_v2_inventory_routes()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/inventory/transactions")).StatusCode);
    }

    private static InventoryOperationRequest Body(InventoryLine line) => new([line]);

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Product, string Bin)> ProductAndBinAsync(HttpClient admin)
    {
        var product = await PostAsync(admin, "/api/v2/products", new ProductCreateRequest("Wire", "1", 1, false, []), Guid.NewGuid(), absolute: true);
        var bin = await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options);
        return ((await product.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id, (await bin.Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!.Id);
    }

    private static Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, Guid key, bool absolute = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, absolute ? path : $"/api/v2/inventory/{path}") { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key.ToString());
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
