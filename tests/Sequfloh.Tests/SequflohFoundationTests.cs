using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohFoundationTests(TestApp app)
{
    [Fact]
    public async Task Module_access_uses_the_existing_session_and_permissions()
    {
        var site = await app.CreateSiteAsync();
        var (_, none) = await app.CreateUserAsync([Permissions.MasterDataView], site);
        var (uid, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewDashboard], site);
        var anon = await app.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/sequfloh/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync("/api/sequfloh/context")).StatusCode);
        var ctx = await user.GetElAsync("/api/sequfloh/context");
        Assert.Equal(uid, ctx.GetProperty("user").Id());
        Assert.Equal("SEQUFLOH", ctx.GetProperty("module").Str("displayName"));
        Assert.Equal("Enterprise Execution Engine", ctx.GetProperty("module").Str("subtitle"));
        Assert.Contains("sequfloh.view_dashboard", ctx.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));
        // a module user cannot change settings, dropdowns or module access
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsync("/api/sequfloh/settings/general", new { value = new { moduleEnabled = false }, rowVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/sequfloh/reference/priorities", new { code = "", label = "Critical" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/sequfloh/module-access")).StatusCode);
        await user.PostOkAsync("/api/sequfloh/access", new { page = "Home" });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.UserId == uid && a.Action == "ModuleAccessed"));
    }

    [Fact]
    public async Task Settings_are_validated_audited_and_can_switch_the_module_off()
    {
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var s = await admin.GetElAsync("/api/sequfloh/settings");
        var general = JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetProperty("general").GetRawText())!;
        general["recordsPerPage"] = 1000;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/sequfloh/settings/general", new { value = general, rowVersion = 0 })).StatusCode);
        var tracking = new { defaultMinimumMinutes = 30, defaultMaximumMinutes = 10 };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 })).StatusCode);
        try
        {
            general["recordsPerPage"] = 50; general["moduleEnabled"] = false;
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync("/api/sequfloh/settings/general", new { value = general, rowVersion = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/sequfloh/settings")).StatusCode);
            Assert.Equal(50, (await admin.GetElAsync("/api/sequfloh/settings")).GetProperty("general").GetProperty("recordsPerPage").GetInt32());
        }
        finally
        {
            general["moduleEnabled"] = true; general["recordsPerPage"] = 25;
            await admin.PutAsync("/api/sequfloh/settings/general", new { value = general, rowVersion = 0 });
        }
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/sequfloh/settings")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityType == "SequflohSettings" && a.Action == "Updated"));
    }

    [Fact]
    public async Task Reference_lists_are_seeded_editable_reorderable_and_never_deleted()
    {
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageDropdowns]);
        var lists = await admin.GetElAsync("/api/sequfloh/reference");
        Assert.True(lists.GetArrayLength() >= 21);
        var descriptions = await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions");
        Assert.Equal(15, descriptions.EnumerateArray().Count(v => v.GetProperty("isSeeded").GetBoolean()));
        var label = "Seal replacement " + Guid.NewGuid().ToString("N")[..6];
        var created = await (await admin.PostOkAsync("/api/sequfloh/reference/repair-descriptions", new { code = "", label, description = "Replace seals" })).JsonAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/sequfloh/reference/repair-descriptions", new { code = "", label })).StatusCode);
        var id = created.Id();
        await admin.PostOkAsync($"/api/sequfloh/reference/values/{id}/default");
        Assert.Equal(label, (await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions/options"))[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/sequfloh/reference/values/{id}/deactivate")).StatusCode); // default first
        var first = (await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions")).EnumerateArray().First(v => v.GetProperty("isSeeded").GetBoolean());
        await admin.PostOkAsync($"/api/sequfloh/reference/values/{first.Id()}/default");
        await admin.PostOkAsync($"/api/sequfloh/reference/values/{id}/deactivate");
        Assert.DoesNotContain(label, (await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions/options")).EnumerateArray().Select(x => x.GetString()));
        var ids = (await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions")).EnumerateArray().Select(v => v.Id()).Reverse().ToList();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/sequfloh/reference/repair-descriptions/reorder", new { ids = ids.Skip(1) })).StatusCode);
        await admin.PostOkAsync("/api/sequfloh/reference/repair-descriptions/reorder", new { ids });
        Assert.Equal(ids[0], (await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions"))[0].Id());
        Assert.True((await admin.GetElAsync($"/api/sequfloh/reference/values/{id}/history")).GetArrayLength() >= 2);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/sequfloh/reference/repair-descriptions/reset", new { confirmed = false })).StatusCode);
        await admin.PostOkAsync("/api/sequfloh/reference/repair-descriptions/reset", new { confirmed = true });
        var after = await admin.GetElAsync("/api/sequfloh/reference/repair-descriptions");
        Assert.Equal("Inspection and diagnosis", after[0].Str("label"));
        Assert.Contains(after.EnumerateArray(), v => v.Id() == id); // administrator-added value kept
        // seeding again adds nothing and keeps administrator changes
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await SequflohFoundationService.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default));
    }

    [Fact]
    public async Task Numbering_patterns_and_revision_formats_are_configurable()
    {
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var seqs = await admin.GetElAsync("/api/sequfloh/numbering");
        Assert.Equal(SequflohCatalog.Numbering.Length, seqs.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/sequfloh/numbering", new { recordType = "SqfPartsList", prefix = "PL-", padding = 4, pattern = "YEAR-ONLY" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync("/api/sequfloh/numbering", new { recordType = "SqfPartsList", prefix = "PL-", padding = 5, pattern = "PREFIX-YEAR-SEQUENCE" })).StatusCode);
        var pl = (await admin.GetElAsync("/api/sequfloh/numbering")).EnumerateArray().First(x => x.Str("recordType") == "SqfPartsList");
        Assert.Matches($"^PL-{DateTime.UtcNow.Year}-\\d{{5}}$", pl.Str("example"));
        await admin.PutAsync("/api/sequfloh/numbering", new { recordType = "SqfPartsList", prefix = "PL-", padding = 4, pattern = "PREFIX-YEAR-SEQUENCE" });
        Assert.Equal("B", SequflohFoundationService.NextRevision("A", "Letters"));
        Assert.Equal("AA", SequflohFoundationService.NextRevision("Z", "Letters"));
        Assert.Equal("01", SequflohFoundationService.NextRevision("00", "TwoDigits"));
        Assert.Equal("00", SequflohFoundationService.FirstRevision("TwoDigits"));
    }

    [Fact]
    public async Task Module_access_changes_only_sequfloh_permissions_of_an_existing_role()
    {
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageModuleAccess]);
        var root = await app.AdminAsync();
        var roleId = await (await root.PostOkAsync("/api/admin/roles", new { name = "Repair bench " + Guid.NewGuid().ToString("N")[..6], description = (string?)null, permissions = new[] { Permissions.MasterDataView } })).Content.ReadFromJsonAsync<Guid>();
        var access = await admin.GetElAsync("/api/sequfloh/module-access");
        Assert.Equal(41, access.GetProperty("permissions").GetArrayLength());
        Assert.Contains(access.GetProperty("suggestions").EnumerateArray(), s => s.Str("name") == "Technician");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/sequfloh/module-access/roles/{roleId}", new { permissions = new[] { Permissions.MasterDataEdit } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/sequfloh/module-access/roles/{roleId}", new { permissions = new[] { Permissions.SequflohStartTask } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/sequfloh/module-access/roles/{roleId}", new { permissions = new[] { Permissions.SequflohAccess, Permissions.SequflohStartTask } })).StatusCode);
        var role = (await root.GetElAsync("/api/admin/roles")).EnumerateArray().First(r => r.Id() == roleId);
        var perms = role.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(Permissions.MasterDataView, perms);
        Assert.Contains(Permissions.SequflohStartTask, perms);
    }

    [Fact]
    public async Task Recent_and_pinned_items_belong_to_the_current_user()
    {
        var (_, a) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var (_, b) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        await a.PostOkAsync("/api/sequfloh/my-items", new { kind = "recent", itemType = "Page", recordId = (Guid?)null, path = "/sequfloh/parts-lists", label = "Parts Lists" });
        await a.PostOkAsync("/api/sequfloh/my-items", new { kind = "favorite", itemType = "Page", recordId = (Guid?)null, path = "/sequfloh/execution/active", label = "Active Station" });
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/sequfloh/my-items", new { kind = "favorite", itemType = "Page", path = "/admin/users", label = "Users" })).StatusCode);
        var mine = await a.GetElAsync("/api/sequfloh/my-items");
        Assert.Equal(1, mine.GetProperty("recent").GetArrayLength());
        var fav = mine.GetProperty("favorites")[0].Id();
        Assert.Equal(0, (await b.GetElAsync("/api/sequfloh/my-items")).GetProperty("favorites").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/sequfloh/my-items/favorites/{fav}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/sequfloh/my-items/favorites/{fav}")).StatusCode);
    }
}
