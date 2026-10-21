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
using Wms.Contracts.V2.Admin;
using Wms.Contracts.V2.Auth;
using Wms.Contracts.V2.Bins;
using Wms.Contracts.V2.Common;

namespace Wms.Api.Tests.V2;

/// <summary>/api/v2/bins, /users, /settings over HTTP in the v2 process mode on temporary files.</summary>
public sealed class V2AdminApiTests : IDisposable
{
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wms-v2-admin-api-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Permission_matrix_follows_the_current_role()
    {
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();
        await AssertProblemAsync(await anonymous.GetAsync("/api/v2/settings"), HttpStatusCode.Unauthorized, ProblemCodesV2.Unauthenticated);

        using var receiver = await SignedInAsync(factory, "receiver");
        Assert.Equal(HttpStatusCode.OK, (await receiver.GetAsync("/api/v2/bins")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await receiver.GetAsync("/api/v2/settings")).StatusCode);
        await AssertProblemAsync(await receiver.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);
        await AssertProblemAsync(await receiver.GetAsync("/api/v2/users"), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);

        using var supervisor = await SignedInAsync(factory, "supervisor");
        Assert.Equal(HttpStatusCode.Created, (await supervisor.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options)).StatusCode);
        var rack = await supervisor.PostAsJsonAsync("/api/v2/bins/racks", new RackCreateRequest("B", "R1", 2, 3), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Created, rack.StatusCode);
        Assert.Equal(6, (await rack.Content.ReadFromJsonAsync<RackCreateResponse>(WmsJson.Options))!.CreatedCount);
        await AssertProblemAsync(await supervisor.GetAsync("/api/v2/users"), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);
        var settings = (await supervisor.GetFromJsonAsync<SettingsResponse>("/api/v2/settings", WmsJson.Options))!;
        await AssertProblemAsync(
            await supervisor.PutAsJsonAsync("/api/v2/settings", With(settings), WmsJson.Options), HttpStatusCode.Forbidden, ProblemCodesV2.Forbidden);

        using var admin = await SignedInAsync(factory, "admin");
        var users = await admin.GetStringAsync("/api/v2/users");
        Assert.DoesNotContain("password", users, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PBKDF2", users, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v2/settings", With(settings), WmsJson.Options)).StatusCode);

        var page = (await admin.GetFromJsonAsync<Page<BinResponse>>("/api/v2/bins?zone=b&level=02&limit=2", WmsJson.Options))!;
        Assert.Equal(new[] { "B-R1-02-01", "B-R1-02-02" }, page.Items.Select(b => b.Code));
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task Account_changes_end_the_sessions_they_affect_immediately()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");

        var created = await admin.PostAsJsonAsync(
            "/api/v2/users", new UserCreateRequest("kim", "Kim", [RoleNames.ShippingClerk], true, "kim password 123"), WmsJson.Options);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var kim = (await created.Content.ReadFromJsonAsync<UserResponse>(WmsJson.Options))!;
        Assert.DoesNotContain("kim password 123", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var kimClient = await SignedInAsync(factory, "kim", "kim password 123");
        Assert.Equal(HttpStatusCode.OK, (await kimClient.GetAsync("/api/v2/auth/me")).StatusCode);

        var reset = await admin.PostAsJsonAsync($"/api/v2/users/{kim.Id}/reset-password", new ResetPasswordRequest("new kim password 456", kim.Version), WmsJson.Options);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        await AssertProblemAsync(await kimClient.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);

        using var again = await SignedInAsync(factory, "kim", "new kim password 456");
        kim = (await reset.Content.ReadFromJsonAsync<UserResponse>(WmsJson.Options))!;
        var update = new UserUpdateRequest("kim", "Kim", [RoleNames.ReceivingClerk], true, kim.Version);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v2/users/{kim.Id}", update, WmsJson.Options)).StatusCode);
        await AssertProblemAsync(await again.GetAsync("/api/v2/auth/me"), HttpStatusCode.Unauthorized, ProblemCodesV2.SessionRevoked);

        var self = (await admin.GetFromJsonAsync<UserResponse>("/api/v2/users/1", WmsJson.Options))!;
        await AssertProblemAsync(
            await admin.PutAsJsonAsync("/api/v2/users/1", new UserUpdateRequest("admin", self.DisplayName, self.Roles, false, self.Version), WmsJson.Options),
            HttpStatusCode.Conflict,
            ProblemCodesV2.LastAdministrator);
    }

    [Fact]
    public async Task Threshold_conflicts_list_the_bins()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var bin = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options))
            .Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!;
        Execute($"UPDATE Bins SET HighThresholdMicros = 30000000 WHERE Id = {bin.Id}");
        var settings = (await admin.GetFromJsonAsync<SettingsResponse>("/api/v2/settings", WmsJson.Options))!;

        var problem = await AssertProblemAsync(
            await admin.PutAsJsonAsync("/api/v2/settings", With(settings) with { LowThreshold = "40" }, WmsJson.Options),
            HttpStatusCode.Conflict,
            ProblemCodesV2.ThresholdConflict);
        Assert.Equal(bin.Id, problem["subjects"]?[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Moving_or_archiving_a_bin_with_an_open_count_or_investigation_answers_the_contract_code()
    {
        using var factory = CreateFactory();
        using var admin = await SignedInAsync(factory, "admin");
        var bin = (await (await admin.PostAsJsonAsync("/api/v2/bins", new BinWriteRequest("A", "R1", "01", "01"), WmsJson.Options))
            .Content.ReadFromJsonAsync<BinResponse>(WmsJson.Options))!;
        Execute(
            "INSERT INTO CountTasks (Number, BinId, BinCode, CreatorId, CreatorName, CreatorRole, StartedAt, Status, CurrentRound) " +
            $"VALUES ('CT-000001', {bin.Id}, 'A-R1-01-01', 1, 'Admin', 'systemAdministrator', 1, 'pendingConfirmation', 1)");
        Execute($"UPDATE Bins SET FrozenCountTaskId = (SELECT Id FROM CountTasks) WHERE Id = {bin.Id}");
        var move = new BinWriteRequest("A", "R1", "01", "02", bin.Version);

        // Frozen by an open count.
        await AssertBlockedAsync(await admin.PutAsJsonAsync($"/api/v2/bins/{bin.Id}", move, WmsJson.Options), bin.Id);
        await AssertBlockedAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/bins/{bin.Id}") { Content = JsonContent.Create(new { }) }), bin.Id);

        // Count finished, investigation unresolved.
        Execute($"UPDATE Bins SET FrozenCountTaskId = NULL WHERE Id = {bin.Id}");
        Execute("UPDATE CountTasks SET Status = 'completed', CompletedAt = 2, HasUnresolvedInvestigation = 1");
        await AssertBlockedAsync(await admin.PutAsJsonAsync($"/api/v2/bins/{bin.Id}", move, WmsJson.Options), bin.Id);
        await AssertBlockedAsync(await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/bins/{bin.Id}") { Content = JsonContent.Create(new { }) }), bin.Id);

        Assert.Equal("A-R1-01-01", (await admin.GetFromJsonAsync<BinResponse>($"/api/v2/bins/{bin.Id}", WmsJson.Options))!.Code);
    }

    [Fact]
    public async Task The_v1_host_does_not_expose_v2_admin_routes()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/bins")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v2/users")).StatusCode);
    }

    private static SettingsUpdateRequest With(SettingsResponse s) =>
        new(s.DefaultRackLevels, s.DefaultCellsPerLevel, s.LowThreshold, s.HighThreshold, s.SessionHours, s.Version);

    private string DatabasePath => Path.Combine(_root, "App_Data", "wms-v2-demo.db");

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("V2:Enabled", "true")
            .UseSetting("V2:Sqlite:DataSource", DatabasePath)
            .UseSetting("V2:Seed:Password", _password)
            .UseSetting("Persistence:Sqlite:DataSource", Path.Combine(_root, "App_Data", "wms-demo.db"))
            .ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(iterations: 1_000))));

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> factory, string username, string? password = null)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(username, password ?? _password), WmsJson.Options);
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

    private static async Task AssertBlockedAsync(HttpResponseMessage response, string binId)
    {
        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, ProblemCodesV2.OpenCountOrInvestigation);
        var subjects = problem["subjects"]!.AsArray().Select(s => $"{s!["type"]!.GetValue<string>()}:{s["id"]!.GetValue<string>()}").ToList();
        Assert.Contains($"bin:{binId}", subjects);
        Assert.Contains(subjects, s => s.StartsWith("countTask:", StringComparison.Ordinal));
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
