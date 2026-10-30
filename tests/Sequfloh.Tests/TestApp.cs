using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

/// <summary>Runs the real API against a local test database that is rebuilt once per test run.</summary>
public sealed class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "correct-horse-battery";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SEQUFLOH_TEST_DB") ?? "Host=localhost;Port=5433;Database=sequfloh_test;Username=sequfloh;Password=sequfloh_local_dev";

    public static readonly string FilesPath = Path.Combine(Path.GetTempPath(), "sequfloh-test-files");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Seed:AdminMustChangePassword", "false");
        builder.UseSetting("Seed:OrganizationName", "Test Organization");
        builder.UseSetting("RateLimits:LoginPerMinute", "100000");
        builder.UseSetting("Storage:Provider", "local");
        builder.UseSetting("Storage:LocalPath", FilesPath);
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public Task<ApiClient> AnonymousAsync() =>
        Task.FromResult(new ApiClient(CreateDefaultClient(new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(), new MadeUpNumbers())));

    public async Task<ApiClient> SignInAsync(string email, string password)
    {
        var c = await AnonymousAsync();
        var r = await c.PostAsync("/api/auth/login", new { email, password });
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Sign-in failed for {email}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return c;
    }

    public Task<ApiClient> AdminAsync() => SignInAsync(AdminEmail, AdminPassword);

    /// <summary>Creates a user and assigns a new role with the given permissions at the given scope.</summary>
    public async Task<(Guid UserId, ApiClient Client)> CreateUserAsync(string[] permissions, Guid? siteId = null, Guid? departmentId = null)
    {
        var admin = await AdminAsync();
        var email = $"u{Guid.NewGuid():N}@test.local";
        var userId = await (await admin.PostOkAsync("/api/admin/users", new { email, displayName = "Test " + email[..9], password = "long-enough-password", mustChangePassword = false })).Content.ReadFromJsonAsync<Guid>();
        var roleId = await (await admin.PostOkAsync("/api/admin/roles", new { name = "R" + Guid.NewGuid().ToString("N"), description = (string?)null, permissions })).Content.ReadFromJsonAsync<Guid>();
        await admin.PostOkAsync($"/api/admin/users/{userId}/roles", new { roleId, siteId, departmentId });
        return (userId, await SignInAsync(email, "long-enough-password"));
    }

    public async Task<Guid> CreateSiteAsync(string? code = null)
    {
        var admin = await AdminAsync();
        code ??= "S" + Guid.NewGuid().ToString("N")[..8];
        return await (await admin.PostOkAsync("/api/organization/sites", new { code, name = "Site " + code, address = (string?)null })).Content.ReadFromJsonAsync<Guid>();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<TestApp>;

public sealed class ApiClient(HttpClient http)
{
    public HttpClient Http => http;

    static HttpRequestMessage Req(HttpMethod m, string url, object? body)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => http.SendAsync(Req(HttpMethod.Post, url, body ?? new { }));
    public Task<HttpResponseMessage> PutAsync(string url, object body) => http.SendAsync(Req(HttpMethod.Put, url, body));
    public Task<HttpResponseMessage> DeleteAsync(string url) => http.SendAsync(Req(HttpMethod.Delete, url, null));

    public async Task<HttpResponseMessage> PostOkAsync(string url, object? body = null)
    {
        var r = await PostAsync(url, body);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"POST {url} failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return r;
    }

    public async Task<HttpResponseMessage> PutOkAsync(string url, object body)
    {
        var r = await PutAsync(url, body);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"PUT {url} failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return r;
    }

    public Task<HttpResponseMessage> UseCrewTimeAsync(Guid workOrderId, bool on = true) =>
        PutOkAsync($"/api/sequfloh/tracking/{workOrderId}/crew/setting", new { useCrewTime = on });

    public async Task<T> GetJsonAsync<T>(string url)
    {
        var r = await http.GetAsync(url);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"GET {url} failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }
}

/// <summary>
/// Records are numbered by the user. Tests that are not about numbering get a made-up unique number on create calls that don't send one, and an
/// imported file gets its file name plus a made-up suffix so repeated test files don't collide. A request with the X-Test-Own-Number header is sent as written.
/// </summary>
public sealed class MadeUpNumbers : DelegatingHandler
{
    static readonly System.Text.RegularExpressions.Regex Creates = new(
        @"^/api/sequfloh/(tracking|instructions|inspection-requests|parts-lists|operation-steps)$|^/api/sequfloh/(instructions|parts-lists|operation-steps)/[0-9a-f-]{36}/duplicate$|^/api/sequfloh/inspection-requests/[0-9a-f-]{36}/convert$|^/api/imported-files/[0-9a-f-]{36}/(fill-out|save-as-copy)$|^/api/sequfloh/chargebacks$");
    static readonly System.Text.RegularExpressions.Regex Uploads = new(@"^/api/imported-files$|^/api/sequfloh/instructions/import$|^/api/imported-files/import-folder$");

    public static string MadeUp() => "T-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Post && !request.Headers.Contains("X-Test-Own-Number"))
        {
            if (Creates.IsMatch(path) && request.Content is { } c && c.Headers.ContentType?.MediaType == "application/json")
            {
                var text = await c.ReadAsStringAsync(ct);
                if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject o)
                {
                    var key = path.EndsWith("/chargebacks") ? "workOrderNumber" : "number";
                    if (!o.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) && (key == "number" || o.Any(p => p.Key.Equals("period", StringComparison.OrdinalIgnoreCase) && p.Value is not null)))
                    {
                        o[key] = MadeUp();
                        request.Content = new StringContent(o.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                    }
                }
            }
            else if (Uploads.IsMatch(path) && request.Content is MultipartFormDataContent form)
            {
                var names = form.Select(p => p.Headers.ContentDisposition?.Name?.Trim('"')).ToList();
                var file = form.Select(p => p.Headers.ContentDisposition?.FileName?.Trim('"')).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                if (!names.Contains("number") && file is not null) form.Add(new StringContent($"{Path.GetFileNameWithoutExtension(file)} {MadeUp()}"), "number");
            }
        }
        return await base.SendAsync(request, ct);
    }
}

internal static class Json
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    public static async Task<JsonElement> GetElAsync(this ApiClient c, string url) => await c.GetJsonAsync<JsonElement>(url);
    public static Guid Id(this JsonElement e, string name = "id") => e.GetProperty(name).GetGuid();
    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public static Task<HttpResponseMessage> UploadAsync(this ApiClient c, string type, Guid id, string fileName, string content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileName.EndsWith(".pdf") ? "application/pdf" : "text/plain");
        form.Add(file, "file", fileName);
        return c.Http.PostAsync($"/api/records/{type}/{id}/files", form);
    }
}
