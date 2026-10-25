using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Saved parts import layouts and each user's Home layout. Made-up data only.</summary>
[Collection("api")]
public sealed class ImportLayoutTests(TestApp app)
{
    const string L = "/api/sequfloh/parts/import/layouts";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static object Layout(string name) => new
    {
        name,
        layout = new
        {
            headerRow = 1, headers = new[] { "OR #", "DWG #", "Position" },
            columns = new Dictionary<string, object> { ["partNumber"] = new { title = "OR #", index = 0 }, ["drawingNumber"] = new { title = "DWG #", index = 1 } },
            @fixed = new Dictionary<string, string> { ["name"] = "Fuel Injector" }, updateExisting = true, keepRepeats = false, removed = new[] { "material" },
        },
    };

    [Fact]
    public async Task A_layout_can_be_saved_read_renamed_and_deleted_and_each_step_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6];
        var saved = await (await eng.PostOkAsync(L, Layout($"Injector list {k}"))).JsonAsync();
        var id = saved.GetProperty("id").GetGuid();

        var all = await eng.GetElAsync(L);
        var one = all.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("layout");
        Assert.Equal("DWG #", one.GetProperty("columns").GetProperty("drawingNumber").GetProperty("title").GetString());
        Assert.Equal("material", one.GetProperty("removed")[0].GetString());
        Assert.True(one.GetProperty("updateExisting").GetBoolean());

        var again = await (await eng.PostOkAsync(L, Layout($"injector LIST {k}"))).JsonAsync();
        Assert.Equal(id, again.GetProperty("id").GetGuid()); // same name replaces it

        var ren = await eng.Http.PutAsJsonAsync($"{L}/{id}/name", new { name = $"Pump list {k}" });
        Assert.True(ren.IsSuccessStatusCode, await ren.Content.ReadAsStringAsync());
        Assert.Contains((await eng.GetElAsync(L)).EnumerateArray(), x => x.GetProperty("name").GetString() == $"Pump list {k}");

        Assert.Equal(HttpStatusCode.NoContent, (await eng.Http.DeleteAsync($"{L}/{id}")).StatusCode);
        Assert.DoesNotContain((await eng.GetElAsync(L)).EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Sequfloh.Infrastructure.Persistence.AppDbContext>();
        var audits = db.AuditEntries.Where(a => a.EntityType == "ImportLayout" && a.EntityId == id).Select(a => a.Action).ToList();
        Assert.Equal(["Create", "Delete", "Rename", "Update"], audits.Order().ToList());
    }

    [Fact]
    public async Task Layouts_need_the_parts_import_permissions()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts], site);
        var id = (await (await eng.PostOkAsync(L, Layout($"Guarded {Guid.NewGuid():N}"))).JsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.GetAsync(L)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync(L, Layout("Nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PutAsJsonAsync($"{L}/{id}/name", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.DeleteAsync($"{L}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Each_user_keeps_their_own_Home_layout_and_can_reset_it()
    {
        var site = await app.CreateSiteAsync();
        var (_, a) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewDashboard], site);
        var (_, b) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewDashboard], site);
        Assert.Equal(JsonValueKind.Null, (await a.GetElAsync("/api/sequfloh/my-home")).GetProperty("sections").ValueKind);

        var order = new[] { new { key = "recent", visible = true }, new { key = "dashboard", visible = false }, new { key = "quick", visible = true }, new { key = "pinned", visible = true } };
        var put = await a.Http.PutAsJsonAsync("/api/sequfloh/my-home", order);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        var mine = (await a.GetElAsync("/api/sequfloh/my-home")).GetProperty("sections");
        Assert.Equal(["recent", "dashboard", "quick", "pinned"], mine.EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList());
        Assert.False(mine[1].GetProperty("visible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await b.GetElAsync("/api/sequfloh/my-home")).GetProperty("sections").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.PutAsJsonAsync("/api/sequfloh/my-home", new[] { new { key = "news", visible = true } })).StatusCode);
        await a.Http.DeleteAsync("/api/sequfloh/my-home");
        Assert.Equal(JsonValueKind.Null, (await a.GetElAsync("/api/sequfloh/my-home")).GetProperty("sections").ValueKind);
    }
}
