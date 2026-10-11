using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Tests.Integration;

/// <summary>
/// Boots the real API against a throwaway SQLite file with the demo seed. Settings are supplied as environment
/// variables because Program reads some of them before the host is built.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin!Test-Password-1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"smartbuy-test-{Guid.NewGuid():N}.db");

    public ApiFactory()
    {
        PasswordHasher.Iterations = 1_000;
        var settings = new Dictionary<string, string>
        {
            ["Database__Provider"] = "sqlite",
            ["Database__ConnectionString"] = $"Data Source={_dbPath}",
            ["Jwt__Key"] = "integration-test-signing-key-0123456789-abcdefghij",
            ["Seed__DemoData"] = "true",
            ["Seed__AdminEmail"] = AdminEmail,
            ["Seed__AdminPassword"] = AdminPassword,
            ["Suppliers__TimeoutSeconds"] = "1",
            ["RateLimits__AuthPerMinute"] = "100000",
            ["RateLimits__GlobalPerMinute"] = "100000",
            ["RateLimits__AiPerMinute"] = "100000",
            ["Billing__AllowManualPlanChange"] = "true",
            ["Ai__Provider"] = "mock",
        };
        foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<TestSession> RegisterAsync(string? organization = null, string? plan = null)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var client = CreateClient();
        var email = $"owner-{id}@example.test";
        const string password = "Sup3r-secret-pass";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password, fullName = "Test Owner", organizationName = organization ?? $"Org {id}", industry = "Plumbing" });
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        var session = new TestSession(client, email, password, body);
        if (plan is not null)
        {
            var change = await session.Client.PostAsJsonAsync("/api/billing/change-plan", new { planCode = plan });
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
        }
        return session;
    }

    public async Task<TestSession> LoginAsync(string email, string password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        return new TestSession(client, email, password, body);
    }

    public async Task<TestSession> DemoAsync(string business)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/demo", new { business });
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        return new TestSession(client, "", "", body);
    }
}

public sealed class TestSession
{
    public TestSession(HttpClient client, string email, string password, JsonNode auth)
    {
        Client = client;
        Email = email;
        Password = password;
        AccessToken = auth["accessToken"]!.GetValue<string>();
        RefreshToken = auth["refreshToken"]!.GetValue<string>();
        OrganizationId = auth["session"]!["organization"]!["id"]!.GetValue<Guid>();
        UserId = auth["session"]!["user"]!["id"]!.GetValue<Guid>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
    }

    public HttpClient Client { get; }
    public string Email { get; }
    public string Password { get; }
    public string AccessToken { get; }
    public string RefreshToken { get; }
    public Guid OrganizationId { get; }
    public Guid UserId { get; }

    public async Task<JsonNode> GetAsync(string url)
    {
        var response = await Client.GetAsync(url);
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {body.ToJsonString()}");
        return body;
    }

    public async Task<JsonNode> PostAsync(string url, object? payload = null)
    {
        var response = await Client.PostAsJsonAsync(url, payload ?? new { });
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {url} -> {(int)response.StatusCode}: {body.ToJsonString()}");
        return body;
    }

    public async Task<JsonNode> PutAsync(string url, object payload)
    {
        var response = await Client.PutAsJsonAsync(url, payload);
        var body = await response.ReadJsonAsync();
        Assert.True(response.IsSuccessStatusCode, $"PUT {url} -> {(int)response.StatusCode}: {body.ToJsonString()}");
        return body;
    }

    /// <summary>Creates a project with one list linked to demo catalog products and approves it.</summary>
    public async Task<(Guid ProjectId, JsonNode Project)> CreateApprovedProjectAsync(params (string Product, decimal Quantity, string Unit)[] lines)
    {
        var items = new List<object>();
        foreach (var (product, quantity, unit) in lines)
        {
            var match = (await GetAsync($"/api/catalog/products?query={Uri.EscapeDataString(product)}")).AsArray().First()!;
            items.Add(new { description = match["name"]!.GetValue<string>(), quantity, unit, canonicalProductId = match["id"]!.GetValue<Guid>() });
        }
        var project = await PostAsync("/api/projects", new { name = $"Project {Guid.NewGuid():N}"[..20], industry = "Plumbing", initialList = new { name = "Materials", items } });
        var listId = project["lists"]![0]!["id"]!.GetValue<Guid>();
        project = await PostAsync($"/api/lists/{listId}/approve");
        return (project["id"]!.GetValue<Guid>(), project);
    }
}

public static class HttpExtensions
{
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject { ["raw"] = text }; }
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
