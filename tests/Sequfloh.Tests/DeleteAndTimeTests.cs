using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class DeleteAndTimeTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static readonly string[] Deleter = [Permissions.SequflohAccess, Permissions.SequflohDeleteRecords, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings,
        Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohStartTask];
    static readonly string[] NoDelete = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles];

    AppDbContext Db(out IServiceScope scope) { scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<AppDbContext>(); }

    async Task<Guid> ReleasedAsync(Guid site, Guid? partId = null)
    {
        var db = Db(out var s); using var _ = s;
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Test instruction", Number = "DWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, PartId = partId };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    async Task<Guid> PartAsync(Guid site)
    {
        var db = Db(out var s); using var _ = s;
        var p = new Part { PartNumber = "DEL-" + Guid.NewGuid().ToString("N")[..8], Name = "Test spacer", SiteId = site, Status = MasterStatus.Active };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    async Task<string?> LastAuditAsync(Guid entityId)
    {
        var db = Db(out var s); using var _ = s;
        return await db.AuditEntries.Where(a => a.EntityId == entityId && a.Action == "Deleted").OrderByDescending(a => a.OccurredAt).Select(a => a.UserName + "|" + a.Description).FirstOrDefaultAsync();
    }

    static byte[] Pdf(string t) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {t}\n%%EOF\n");

    [Fact]
    public async Task Delete_needs_the_permission_for_the_site_and_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var (_, deleter) = await app.CreateUserAsync(Deleter, site);
        var (_, plain) = await app.CreateUserAsync(NoDelete, site);
        var wi = await ReleasedAsync(site);
        var elsewhere = await ReleasedAsync(other);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync($"/api/sequfloh/instructions/{wi}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deleter.DeleteAsync($"/api/sequfloh/instructions/{elsewhere}")).StatusCode);
        var ok = await deleter.DeleteAsync($"/api/sequfloh/instructions/{wi}");
        Assert.True(ok.StatusCode == HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await deleter.DeleteAsync($"/api/sequfloh/instructions/{wi}")).StatusCode);
        var audit = await LastAuditAsync(wi);
        Assert.NotNull(audit);
        Assert.StartsWith("Test ", audit);
        Assert.Contains("Deleted work instruction DWI-", audit);
    }

    [Fact]
    public async Task Work_orders_with_logged_time_and_their_instructions_are_kept()
    {
        var site = await app.CreateSiteAsync();
        var (techId, deleter) = await app.CreateUserAsync(Deleter, site);
        var wi = await ReleasedAsync(site);
        var empty = (await (await deleter.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        var worked = (await (await deleter.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        await deleter.StartAsync(worked);

        var conflict = await deleter.DeleteAsync($"/api/sequfloh/instructions/{wi}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("obsolete", await conflict.Content.ReadAsStringAsync());

        await deleter.PostOkAsync($"{U}/{worked}/time/start");
        await deleter.PostOkAsync($"{U}/{worked}/time/stop");
        var blocked = await deleter.DeleteAsync($"{U}/{worked}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("chargebacks", await blocked.Content.ReadAsStringAsync());
        Assert.False((await deleter.GetElAsync($"{U}/{worked}")).GetProperty("canDelete").GetBoolean());

        Assert.True((await deleter.GetElAsync($"{U}/{empty}")).GetProperty("canDelete").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await deleter.DeleteAsync($"{U}/{empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await deleter.GetAsync($"{U}/{empty}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_part_removes_drawing_links_but_keeps_drawings_and_parts_in_use_are_kept()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Deleter, site);
        var part = await PartAsync(site);
        var used = await PartAsync(site);
        await ReleasedAsync(site, used);
        var folder = (await (await c.PostOkAsync("/api/drawings/folders", new { name = "Spacers", siteId = site })).JsonAsync()).Id();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Pdf("a")), "file", "spacer.pdf" }, { new StringContent("Spacer"), "title" }, { new StringContent("A"), "revision" }, { new StringContent(part.ToString()), "partIds" } };
        var drawing = (await (await c.Http.PostAsync($"/api/drawings/folders/{folder}/drawings", form)).JsonAsync()).Id();

        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/sequfloh/parts/{used}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/sequfloh/parts/{part}")).StatusCode);
        var d = await c.GetElAsync($"/api/drawings/{drawing}");
        Assert.Equal("Spacer", d.Str("title"));
        Assert.Contains("links to 1 drawing", (await LastAuditAsync(part))!);

        // a folder that still holds a drawing is kept; the last revision can't go on its own
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/drawings/folders/{folder}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/drawings/{drawing}/revisions/A")).StatusCode);
        var rev = new MultipartFormDataContent { { new ByteArrayContent(Pdf("b")), "file", "spacer-b.pdf" }, { new StringContent("B"), "revision" } };
        Assert.True((await c.Http.PostAsync($"/api/drawings/{drawing}/revisions", rev)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/drawings/{drawing}/revisions/B")).StatusCode);
        Assert.Equal("A", (await c.GetElAsync($"/api/drawings/{drawing}")).Str("currentRevision"));

        string[] keys;
        { var db = Db(out var s); using var _ = s; keys = await db.DrawingRevisions.Where(r => r.DrawingId == drawing).Select(r => r.StorageKey).ToArrayAsync(); }
        Assert.All(keys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/drawings/{drawing}")).StatusCode);
        Assert.All(keys, k => Assert.False(File.Exists(Path.Combine(TestApp.FilesPath, k)))); // stored files removed too
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/drawings/folders/{folder}")).StatusCode);
    }

    [Fact]
    public async Task Blank_form_with_filled_copies_is_kept_until_the_copies_are_deleted()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Deleter, site);
        var form = new MultipartFormDataContent { { new ByteArrayContent(ImportTests.InspectionWorkbook()), "file", "Made-up checks.xlsx" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } };
        var blank = (await (await c.Http.PostAsync("/api/imported-files", form)).JsonAsync()).Id();
        var copy = (await (await c.PostOkAsync($"/api/imported-files/{blank}/fill-out", new { title = "Copy 1" })).JsonAsync()).Id();
        var r = await c.DeleteAsync($"/api/imported-files/{blank}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("filled copy", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/imported-files/{copy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/imported-files/{blank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/imported-files/{blank}")).StatusCode);
    }

    [Fact]
    public async Task Sites_and_departments_in_use_are_kept()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var dept = await (await admin.PostOkAsync($"/api/organization/sites/{site}/departments", new { code = "D1", name = "Test cell" })).Content.ReadFromJsonAsync<Guid>();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/organization/sites/{site}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/organization/departments/{dept}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/organization/sites/{site}")).StatusCode);
    }

    [Fact]
    public async Task Several_people_are_assigned_and_anyone_can_log_time_with_a_per_person_total()
    {
        var site = await app.CreateSiteAsync();
        var (leadId, planner) = await app.CreateUserAsync(Deleter, site);
        var (helperId, helper) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, stranger) = await app.CreateUserAsync([], null); // no permissions at all, not assigned
        var wi = await ReleasedAsync(site);
        var id = (await (await planner.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site, assigneeIds = new[] { leadId, helperId } })).JsonAsync()).Id();
        var rec = await planner.GetElAsync($"{U}/{id}");
        Assert.Equal("Assigned", rec.GetProperty("record").Str("status"));
        Assert.Equal(2, rec.GetProperty("record").GetProperty("assignees").GetArrayLength());

        // the helper sees it under My work even though the lead is someone else
        var mine = await helper.GetElAsync($"{U}?folder=mine");
        Assert.Contains(mine.GetProperty("items").EnumerateArray(), x => x.Id() == id);
        var list = await planner.GetElAsync($"{U}?q={rec.GetProperty("record").Str("number")}");
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("assignees").GetArrayLength());

        // assignees can be changed; the change is a task event and an audit entry
        var put = await planner.PutAsync($"{U}/{id}/assignees", new { userIds = new[] { helperId } });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.PutAsync($"{U}/{id}/assignees", new { userIds = new[] { leadId } })).StatusCode);

        // timers: three people at once, including one without any permission
        await planner.StartAsync(id);
        await planner.PostOkAsync($"{U}/{id}/time/start");
        await helper.PostOkAsync($"{U}/{id}/time/start");
        await stranger.PostOkAsync($"{U}/{id}/time/start");
        Assert.Equal(HttpStatusCode.Conflict, (await helper.PostAsync($"{U}/{id}/time/start")).StatusCode);
        { var db = Db(out var s); using var _ = s; await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sqf.sequfloh_time_entries SET \"StartedAt\" = \"StartedAt\" - interval '10 minutes' WHERE \"TrackingRecordId\" = {id} AND \"UserId\" = {helperId}"); }
        await helper.PostOkAsync($"{U}/{id}/time/stop");
        Assert.Equal(HttpStatusCode.Conflict, (await helper.PostAsync($"{U}/{id}/time/stop")).StatusCode);
        var t = await stranger.GetElAsync($"{U}/{id}/time");
        Assert.Equal(3, t.GetProperty("people").GetArrayLength());
        Assert.True(t.GetProperty("mineRunning").GetBoolean());
        var helperRow = t.GetProperty("people").EnumerateArray().First(p => p.Id("userId") == helperId);
        Assert.InRange(helperRow.GetProperty("seconds").GetInt64(), 600, 660);
        Assert.True(t.GetProperty("totalSeconds").GetInt64() >= 600);
        await planner.PostOkAsync($"{U}/{id}/time/stop");
        await stranger.PostOkAsync($"{U}/{id}/time/stop");

        var db2 = Db(out var s2); using var __ = s2;
        Assert.True(await db2.AuditEntries.AnyAsync(a => a.EntityType == "SequflohTimeEntry" && a.Action == "Created" && a.Description.Contains("time on")));
        Assert.True(await db2.AuditEntries.AnyAsync(a => a.EntityId == id && a.Description.Contains("assigned people changed")));
    }
}
