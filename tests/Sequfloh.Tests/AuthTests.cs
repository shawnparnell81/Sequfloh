using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class AuthTests(TestApp app)
{
    [Fact]
    public async Task Api_needs_a_session_and_wrong_passwords_are_refused_then_lock_the_account()
    {
        var anon = await app.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/sequfloh/context")).StatusCode);
        var session = await anon.GetElAsync("/api/auth/session");
        Assert.False(session.GetProperty("authenticated").GetBoolean());
        Assert.False(session.GetProperty("needsSetup").GetBoolean());

        var (_, _) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var admin = await app.AdminAsync();
        var email = $"lock{Guid.NewGuid():N}@test.local";
        await admin.PostOkAsync("/api/admin/users", new { email, displayName = "Lock Test", password = "long-enough-password", mustChangePassword = false });
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/auth/login", new { email, password = "wrong-password-here" })).StatusCode);
        // locked now, even with the right password
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/auth/login", new { email, password = "long-enough-password" })).StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_ends_when_the_browser_closes()
    {
        var raw = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var r = await raw.PostAsJsonAsync("/api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSettings.CookieName + "="));
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remember_me_keeps_the_sign_in_for_30_days_and_is_audited()
    {
        var raw = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var r = await raw.PostAsJsonAsync("/api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword, rememberMe = true });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSettings.CookieName + "="));
        var m = System.Text.RegularExpressions.Regex.Match(cookie, "expires=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(m.Success);
        var days = (DateTimeOffset.Parse(m.Groups[1].Value) - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(days, 29.9, 30.1);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken((await r.JsonAsync()).Str("token"));
        Assert.InRange((token.ValidTo - DateTime.UtcNow).TotalDays, 29.9, 30.1);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.AuditEntries.Where(a => a.Action == "SignedIn").OrderByDescending(a => a.OccurredAt).FirstAsync();
        Assert.Contains("Remember me", entry.Description);
        Assert.NotNull(entry.UserId);

        // unticked stays a session cookie with the short token
        var plain = await raw.PostAsJsonAsync("/api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword, rememberMe = false });
        Assert.DoesNotContain("expires=", plain.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthSettings.CookieName + "=")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passwords_are_hashed_and_a_bearer_token_works_for_api_clients()
    {
        var anon = await app.AnonymousAsync();
        var r = await anon.PostAsync("/api/auth/login", new { email = TestApp.AdminEmail, password = TestApp.AdminPassword });
        var token = (await r.JsonAsync()).Str("token");
        var raw = app.CreateClient();
        raw.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await raw.GetAsync("/api/admin/users")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = await db.Users.Where(u => u.Email == TestApp.AdminEmail).Select(u => u.PasswordHash).SingleAsync();
        Assert.DoesNotContain(TestApp.AdminPassword, hash);
        Assert.True(hash.Length > 40);
    }

    [Fact]
    public async Task Changing_a_password_or_deactivating_ends_earlier_sessions()
    {
        var (uid, user) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/sequfloh/context")).StatusCode);
        var second = await app.SignInAsync((await SessionEmailAsync(user)), "long-enough-password");
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync("/api/auth/change-password", new { currentPassword = "long-enough-password", newPassword = "another-long-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/sequfloh/context")).StatusCode); // this session got a new token
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/sequfloh/context")).StatusCode);

        // the user's real email and current password work while the account is active
        var email = await SessionEmailAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await (await app.AnonymousAsync()).PostAsync("/api/auth/login", new { email, password = "another-long-password" })).StatusCode);

        var admin = await app.AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/admin/users/{uid}", new { displayName = "Gone", jobTitle = (string?)null, defaultSiteId = (Guid?)null, isActive = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/sequfloh/context")).StatusCode);
        // the same correct credentials are refused once the account is deactivated
        var refused = await (await app.AnonymousAsync()).PostAsync("/api/auth/login", new { email, password = "another-long-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain("token", await refused.Content.ReadAsStringAsync());
        // and accepted again after reactivation, so the refusal came from the deactivation alone
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/admin/users/{uid}", new { displayName = "Back", jobTitle = (string?)null, defaultSiteId = (Guid?)null, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await app.AnonymousAsync()).PostAsync("/api/auth/login", new { email, password = "another-long-password" })).StatusCode);
    }

    static async Task<string> SessionEmailAsync(ApiClient c) => (await c.GetElAsync("/api/auth/session")).GetProperty("user").Str("email");

    [Fact]
    public async Task People_and_roles_screens_need_their_own_permissions()
    {
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync("/api/admin/roles", new { name = "Sneaky", permissions = new[] { Permissions.AdminUsers } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync("/api/organization/sites", new { code = "X1", name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/audit-trail")).StatusCode);

        var (_, people) = await app.CreateUserAsync([Permissions.AdminUsers]);
        Assert.Equal(HttpStatusCode.OK, (await people.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await people.GetAsync("/api/admin/roles")).StatusCode);
    }

    [Fact]
    public async Task Default_roles_come_with_suggested_permissions_that_an_administrator_can_change_and_changes_are_audited()
    {
        var admin = await app.AdminAsync();
        var roles = (await admin.GetElAsync("/api/admin/roles")).EnumerateArray().ToList();
        var names = roles.Select(r => r.Str("name")).ToList();
        foreach (var n in new[] { "Administrator", "Operator", "Technician", "Supervisor", "Quality", "Engineering", "Document control", "Module administrator" }) Assert.Contains(n, names);
        var op = roles.Single(r => r.Str("name") == "Operator");
        var perms = op.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Contains(Permissions.SequflohStartTask, perms);
        Assert.DoesNotContain(Permissions.AdminUsers, perms);

        perms.Add(Permissions.SequflohHoldTask);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/admin/roles/{op.Id()}", new { name = "Operator", description = op.Str("description"), permissions = perms })).StatusCode);
        var after = (await admin.GetElAsync("/api/admin/roles")).EnumerateArray().Single(r => r.Id() == op.Id()).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(Permissions.SequflohHoldTask, after);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/admin/roles/{op.Id()}", new { name = "Operator", permissions = new[] { "made.up" } })).StatusCode);

        var adminRole = roles.Single(r => r.Str("name") == "Administrator");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/admin/roles/{adminRole.Id()}", new { name = "Administrator", permissions = new[] { Permissions.SequflohAccess } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/admin/roles/{adminRole.Id()}")).StatusCode);

        var trail = await admin.GetElAsync("/api/audit-trail?action=PermissionsChanged");
        var entry = trail.GetProperty("items").EnumerateArray().First(e => e.GetProperty("entityId").GetGuid() == op.Id());
        Assert.Equal("Administrator", entry.Str("who"));
        Assert.Equal("PermissionsChanged", entry.Str("what"));
        Assert.True(entry.GetProperty("when").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Contains("added Hold task", entry.Str("description"));
    }

    [Fact]
    public async Task Permissions_are_scoped_to_the_site_they_were_granted_for()
    {
        var site = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction], site);
        Assert.True((await user.PostAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Allowed", siteId = site })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Elsewhere", siteId = other })).StatusCode);
    }
}

/// <summary>First run against an empty database: the set-up page creates the first administrator once.</summary>
public sealed class FirstRunTests : IAsyncLifetime
{
    sealed class EmptyApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", TestApp.ConnectionString.Replace("Database=sequfloh_test", "Database=sequfloh_firstrun_test"));
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Storage:LocalPath", TestApp.FilesPath);
        }
    }

    readonly EmptyApp _app = new();

    public async Task InitializeAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Setup_creates_the_first_administrator_only_once()
    {
        var c = new ApiClient(_app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        Assert.True((await c.GetElAsync("/api/auth/setup")).GetProperty("needsSetup").GetBoolean());
        var body = new { organizationName = "Acme Fab", siteCode = "GVL", siteName = "Greenville", timeZone = "America/New_York", displayName = "First Admin", email = "first@test.local", password = "short" };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/auth/setup", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/setup", body with { password = "a-long-first-password" })).StatusCode);
        var me = await c.GetElAsync("/api/auth/session");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Contains(me.GetProperty("user").GetProperty("grants").EnumerateArray(), g => g.Str("permission") == Permissions.AdminRoles);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/users")).StatusCode);
        var again = new ApiClient(_app.CreateClient());
        Assert.False((await again.GetElAsync("/api/auth/setup")).GetProperty("needsSetup").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await again.PostAsync("/api/auth/setup", body with { email = "second@test.local", password = "a-long-first-password" })).StatusCode);
        var trail = await c.GetElAsync("/api/audit-trail?action=SetupCompleted");
        Assert.Equal(1, trail.GetProperty("total").GetInt32());
    }
}
