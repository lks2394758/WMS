using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Wms.Application.Auth;
using Wms.Contracts.Common;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Catalog;
using Wms.Contracts.V2.Common;

namespace Wms.Api.Tests.V2;

/// <summary>
/// /api/v2/units, /categories, /products over HTTP in the v2 process mode on temporary files: permissions by current
/// role, create retries with Idempotency-Key, wire formats (string IDs, no images in lists), problem codes with subjects,
/// and that the v1 host does not expose any of it.
/// </summary>
public sealed class V2CatalogApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-catalog-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Reads_need_a_session_and_writes_need_catalog_write_of_the_current_role()
    {
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();
        await AssertProblemAsync(await anonymous.GetAsync("/api/v2/units"), HttpStatusCode.Unauthorized, ProblemCodesV2.Unauthenticated);

        using var receiver = await SignedInAsync(factory, "receiver");
        Assert.Equal(HttpStatusCode.OK, (await receiver.GetAsync("/api/v2/units")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await receiver.GetAsync("/api/v2/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await receiver.GetAsync("/api/v2/products")).StatusCode);
        await AssertProblemAsync(
            await receiver.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("Box"), WmsJson.Options),
            HttpStatusCode.Forbidden,
            ProblemCodesV2.Forbidden);

        using var supervisor = await SignedInAsync(factory, "supervisor");
        var created = await supervisor.PostAsJsonAsync("/api/v2/units", new UnitWriteRequest("Box"), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var unit = (await created.Content.ReadFromJsonAsync<UnitResponse>(WmsJson.Options))!;
        Assert.True(WireId.IsValid(unit.Id));
        Assert.Equal(1, unit.Version);
    }

    [Fact]
    public async Task Product_create_requires_a_key_and_replays_the_same_body()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var body = new ProductCreateRequest("Scanner", "1", 0, true, []);

        var missing = await admin.PostAsJsonAsync("/api/v2/products", body, WmsJson.Options);
        var problem = await AssertProblemAsync(missing, HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        Assert.NotNull(problem["errors"]?["Idempotency-Key"]);

        var key = Guid.NewGuid();
        var first = await PostWithKeyAsync(admin, "/api/v2/products", body, key);
        var retry = await PostWithKeyAsync(admin, "/api/v2/products", body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains(ApiHeaders.IdempotentReplayed));
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(ApiHeaders.IdempotentReplayed)));
        var original = (await first.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!;
        Assert.Equal(original.Id, (await retry.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id);
        Assert.Equal("SKU-000001", original.Sku);
        Assert.Equal("0", original.TotalQuantity);

        await AssertProblemAsync(
            await PostWithKeyAsync(admin, "/api/v2/products", body with { Name = "Different" }, key),
            HttpStatusCode.Conflict,
            ProblemCodesV2.IdempotencyConflict);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM Products"));
    }

    [Fact]
    public async Task Lists_never_carry_images_and_details_do()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var image = Convert.ToBase64String(ValidPng());
        var created = await PostWithKeyAsync(
            admin, "/api/v2/products", new ProductCreateRequest("Photo", "1", 0, false, [], ImageBase64: image, ImageContentType: "image/png"), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<ProductResponse>(WmsJson.Options))!.Id;

        var list = await admin.GetStringAsync("/api/v2/products?search=pho&limit=10");
        Assert.DoesNotContain("imageBase64", list, StringComparison.Ordinal);
        Assert.DoesNotContain(image, list, StringComparison.Ordinal);
        Assert.Contains("\"hasImage\":true", list, StringComparison.Ordinal);

        var detail = (await admin.GetFromJsonAsync<ProductResponse>($"/api/v2/products/{id}", WmsJson.Options))!;
        Assert.Equal(image, detail.ImageBase64);
        Assert.Equal("image/png", detail.ImageContentType);

        var svg = Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
        await AssertProblemAsync(
            await PostWithKeyAsync(admin, "/api/v2/products", new ProductCreateRequest("Fake", "1", 0, false, [], ImageBase64: svg, ImageContentType: "image/png"), Guid.NewGuid()),
            HttpStatusCode.UnprocessableEntity,
            ProblemCodesV2.InvalidImage);
    }

    [Fact]
    public async Task Problems_carry_stable_codes_fields_and_subjects()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");

        var preset = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v2/units/1") { Content = JsonContent.Create(new { }) });
        var inUse = await AssertProblemAsync(preset, HttpStatusCode.Conflict, ProblemCodesV2.InUse);
        Assert.Equal("1", inUse["subjects"]?[0]?["id"]?.GetValue<string>());

        await AssertProblemAsync(await admin.GetAsync("/api/v2/products/abc"), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);
        await AssertProblemAsync(await admin.GetAsync("/api/v2/products/999"), HttpStatusCode.NotFound, ProblemCodesV2.NotFound);
        await AssertProblemAsync(await admin.GetAsync("/api/v2/products?limit=0"), HttpStatusCode.BadRequest, ProblemCodesV2.ValidationFailed);

        var first = await PostWithKeyAsync(admin, "/api/v2/products", new ProductCreateRequest("A", "1", 0, false, [], Sku: "DUP-1"), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var duplicate = await AssertProblemAsync(
            await PostWithKeyAsync(admin, "/api/v2/products", new ProductCreateRequest("B", "1", 0, false, [], Sku: "dup-1"), Guid.NewGuid()),
            HttpStatusCode.Conflict,
            ProblemCodesV2.AlreadyExists);
        Assert.NotNull(duplicate["errors"]?["sku"]);
    }

    [Fact]
    public async Task Non_ascii_product_names_are_found_case_insensitively()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var created = await PostWithKeyAsync(admin, "/api/v2/products", new ProductCreateRequest("écrou", "1", 0, false, []), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var upper = await admin.GetFromJsonAsync<Page<ProductListItem>>($"/api/v2/products?search={Uri.EscapeDataString("ÉCROU")}", WmsJson.Options);
        var lower = await admin.GetFromJsonAsync<Page<ProductListItem>>($"/api/v2/products?search={Uri.EscapeDataString("écrou")}", WmsJson.Options);

        Assert.Equal("écrou", Assert.Single(upper!.Items).Name);
        Assert.Single(lower!.Items);
    }

    [Fact]
    public async Task The_v1_host_does_not_expose_v2_catalog_routes()
    {
        using var factory = new WebApplicationFactory<Program>(); // legacy mode (in-memory provider for tests)
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/units")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/products")).StatusCode);
        Assert.DoesNotContain("/api/v2/", await client.GetStringAsync("/openapi/v1.json"), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("V2:Enabled", "true")
            .UseSetting("V2:Sqlite:DataSource", DatabasePath)
            .UseSetting("V2:Seed:Password", _password)
            .UseSetting("Persistence:Sqlite:DataSource", Path.Combine(_root, "App_Data", "wms-demo.db"))
            .ConfigureTestServices(services =>
                services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000))));

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, string username)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(username, _password), WmsJson.Options);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(WmsJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    private static Task<HttpResponseMessage> PostWithKeyAsync<T>(HttpClient client, string path, T body, Guid key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: WmsJson.Options) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key.ToString());
        return client.SendAsync(request);
    }

    private static async Task<JsonNode> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(code, problem["code"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(problem["traceId"]?.GetValue<string>()));
        return problem;
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A real 1x1 RGBA PNG with correct CRCs and zlib data.</summary>
    private static byte[] ValidPng()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var chunk = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            var crc = 0xFFFFFFFFu;
            foreach (var b in chunk.AsSpan(4, 4 + data.Length))
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), crc ^ 0xFFFFFFFFu);
            return chunk;
        }

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write([0, 10, 20, 30, 255]);
        }

        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return [.. signature, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
    }
}
