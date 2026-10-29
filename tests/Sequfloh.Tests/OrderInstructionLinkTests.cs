using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Work instructions on work orders: uploaded-file instructions, opening the file inline, and Remove WI from order with a reason. Made-up data only.</summary>
[Collection("api")]
public sealed class OrderInstructionLinkTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask,
        Permissions.SequflohStartTask, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction];

    async Task<Guid> ReleasedAsync(Guid site, WorkInstructionType type, string steps, bool uploaded = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = type, Title = "Example instruction", Number = "TWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, Steps = steps, IsUploadedFile = uploaded };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }
    static async Task<JsonElement> Rec(ApiClient c, Guid id) => await c.GetElAsync($"{U}/{id}");
    async Task Start(ApiClient c, Guid id)
    {
        var r = await Rec(c, id);
        await c.PostOkAsync($"{U}/{id}/actions/start", new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("record").GetProperty("stateVersion").GetInt32() });
    }

    [Fact]
    public async Task An_imported_inspection_file_is_flagged_releases_without_steps_and_loads_no_steps_onto_an_order()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Example inspection: check the housing."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "Example housing inspection.txt"); form.Add(new StringContent("Inspection"), "type"); form.Add(new StringContent(site.ToString()), "siteId");
        var resp = await c.Http.PostAsync("/api/sequfloh/instructions/import", form);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var id = (await resp.JsonAsync()).Id();
        var wi = await c.GetElAsync($"/api/sequfloh/instructions/{id}");
        Assert.True(wi.GetProperty("instruction").GetProperty("isUploadedFile").GetBoolean());
        // only the file is checked: no inspection step is needed to release it
        Assert.DoesNotContain(wi.GetProperty("releaseProblems").EnumerateArray(), p => p.GetString()!.Contains("inspection step"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // stored built-in rows (for example an old placeholder step) stay stored but are not copied onto orders
            await db.SequflohWorkInstructions.Where(w => w.Id == id).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WorkInstructionStatus.Released)
                .SetProperty(w => w.Steps, """[{"id":"33333333-3333-3333-3333-333333333333","sequence":10,"title":"Inspect"}]"""));
        }
        var order = (await (await c.PostOkAsync(U, new { type = "Inspection", workInstructionId = id, siteId = site })).JsonAsync()).Id();
        var r = await Rec(c, order);
        Assert.Equal(0, r.GetProperty("record").GetProperty("steps").GetArrayLength());
        Assert.True(r.GetProperty("instruction").GetProperty("isUploadedFile").GetBoolean());
        var fid = r.GetProperty("instruction").GetProperty("file").Str("id");
        // the file opens in the browser tab, not as a download, and only for a signed-in user who can see the instruction
        var open = await c.Http.GetAsync($"/api/files/{fid}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal("inline", open.Content.Headers.ContentDisposition?.DispositionType);
        Assert.StartsWith("text/plain", open.Content.Headers.ContentType?.ToString());
        var (_, stranger) = await app.CreateUserAsync([Permissions.SequflohAccess], await app.CreateSiteAsync());
        Assert.NotEqual(HttpStatusCode.OK, (await stranger.Http.GetAsync($"/api/files/{fid}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await app.CreateClient().GetAsync($"/api/files/{fid}")).StatusCode);
    }

    [Fact]
    public async Task Remove_WI_from_order_needs_its_permission_and_a_reason_keeps_recorded_steps_and_relinks_the_current_revision()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var (_, remover) = await app.CreateUserAsync([.. Planner, Permissions.SequflohRemoveOrderInstruction], site);
        var wiA = await ReleasedAsync(site, WorkInstructionType.Validation, """[{"id":"11111111-1111-1111-1111-111111111111","sequence":10,"title":"Pressurize"},{"id":"22222222-2222-2222-2222-222222222222","sequence":20,"title":"Leak check"}]""");
        var wiB = await ReleasedAsync(site, WorkInstructionType.Validation, """[{"id":"44444444-4444-4444-4444-444444444444","sequence":10,"title":"Pressurize to the new limit"}]""");
        var order = (await (await c.PostOkAsync(U, new { type = "Validation", workInstructionId = wiA, siteId = site })).JsonAsync()).Id();
        await Start(c, order);
        var rec = (await Rec(c, order)).GetProperty("record");
        var s1 = rec.GetProperty("steps")[0].Str("id");
        Assert.True((await c.PutAsync($"{U}/{order}", new { operationId = Guid.NewGuid(), stateVersion = rec.GetProperty("stateVersion").GetInt32(), steps = new[] { new { id = s1, result = "Pass", comment = "Held 5 min" } } })).IsSuccessStatusCode);
        // after the start the instruction can't just be swapped
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsync($"{U}/{order}/instruction", new { workInstructionId = wiB })).StatusCode);
        Assert.False((await Rec(c, order)).GetProperty("canRemoveInstruction").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"{U}/{order}/instruction/remove", new { reason = "Needs an edit" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await remover.PostAsync($"{U}/{order}/instruction/remove", new { reason = " " })).StatusCode);
        Assert.True((await Rec(remover, order)).GetProperty("canRemoveInstruction").GetBoolean());
        await remover.PostOkAsync($"{U}/{order}/instruction/remove", new { reason = "Limit changes in the next revision" });
        var after = (await Rec(c, order)).GetProperty("record");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("workInstructionId").ValueKind);
        var steps = after.GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength()); // the untouched step is taken off, the recorded one stays
        Assert.Equal("Pass", steps[0].Str("result"));
        Assert.Contains("rev A", steps[0].Str("keptFrom"));
        // relinked: the current revision's steps load after the kept one
        Assert.True((await c.PutAsync($"{U}/{order}/instruction", new { workInstructionId = wiB })).IsSuccessStatusCode);
        steps = (await Rec(c, order)).GetProperty("record").GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("Pressurize to the new limit", steps[1].Str("title"));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audits = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == order).ToListAsync();
        Assert.Contains(audits, a => a.Action == "TrackingInstructionRemoved" && a.Description!.Contains("Reason: Limit changes in the next revision") && a.Description.Contains("1 row(s) with recorded results kept") && a.UserName != null);
        Assert.Contains(audits, a => a.Action == "TrackingInstructionChanged" && a.Description!.Contains("from none to"));
    }

    [Fact]
    public async Task A_closed_order_keeps_its_instruction()
    {
        var site = await app.CreateSiteAsync();
        var (_, remover) = await app.CreateUserAsync([.. Planner, Permissions.SequflohRemoveOrderInstruction, Permissions.SequflohCancelTask], site);
        var wi = await ReleasedAsync(site, WorkInstructionType.Validation, """[{"id":"55555555-5555-5555-5555-555555555555","sequence":10,"title":"Check"}]""");
        var order = (await (await remover.PostOkAsync(U, new { type = "Validation", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTrackingRecords.Where(x => x.Id == order).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, TrackingStatus.Completed));
        Assert.False((await Rec(remover, order)).GetProperty("canRemoveInstruction").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await remover.PostAsync($"{U}/{order}/instruction/remove", new { reason = "Edit" })).StatusCode);
    }
}
