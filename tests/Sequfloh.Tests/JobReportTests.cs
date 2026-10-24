using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class JobReportTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohStartTask,
        Permissions.SequflohCompleteTask, Permissions.SequflohEditParts, Permissions.SequflohViewAuditTrail];
    static readonly string[] Plain = [Permissions.SequflohAccess];

    async Task<Guid> ReleasedAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Made-up repair", Number = "JWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    async Task<Guid> PartAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = new Part { PartNumber = "JOB-" + Guid.NewGuid().ToString("N")[..8], Name = "Made-up assembly", SiteId = site, Status = MasterStatus.Active };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{U}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
    static Task<HttpResponseMessage> Act(ApiClient c, Guid id, string action, int v, string? reason = null) =>
        c.PostAsync($"{U}/{id}/actions/{action}", new { operationId = Guid.NewGuid(), stateVersion = v, reason });

    async Task<(Guid Site, Guid Id, ApiClient Planner, ApiClient Plain)> JobAsync()
    {
        var site = await app.CreateSiteAsync();
        var (plannerId, planner) = await app.CreateUserAsync(Planner, site);
        var (_, plain) = await app.CreateUserAsync(Plain, site);
        var wi = await ReleasedAsync(site);
        var id = (await (await planner.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        await planner.PostOkAsync($"{U}/{id}/actions/dispatch", new { operationId = Guid.NewGuid(), stateVersion = await Ver(planner, id), assignedUserId = plannerId });
        return (site, id, planner, plain);
    }

    [Fact]
    public async Task Anyone_who_can_see_a_work_order_can_work_it_without_being_assigned()
    {
        var (_, id, _, plain) = await JobAsync();
        var view = await plain.GetElAsync($"{U}/{id}");
        Assert.Contains(view.GetProperty("actions").EnumerateArray(), a => a.GetProperty("action").GetString() == "start");
        var start = await Act(plain, id, "start", await Ver(plain, id));
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
        Assert.True((await plain.GetElAsync($"{U}/{id}")).GetProperty("canEnter").GetBoolean());
        await plain.PostOkAsync($"{U}/{id}/time/start");
        Assert.True((await Act(plain, id, "pause", await Ver(plain, id), "Break")).IsSuccessStatusCode);
        Assert.True((await Act(plain, id, "resume", await Ver(plain, id))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(plain, id, "complete", await Ver(plain, id))).StatusCode);
        Assert.False((await Act(plain, id, "hold", await Ver(plain, id), "x")).IsSuccessStatusCode); // holds are kept in the ERP
        var time = await plain.GetElAsync($"{U}/{id}/time");
        Assert.Single(time.GetProperty("people").EnumerateArray());
    }

    [Fact]
    public async Task Job_quantity_and_parts_replaced_feed_the_report_and_are_audited()
    {
        var (_, id, planner, plain) = await JobAsync();
        await plain.PostOkAsync($"{U}/{id}/job/quantity", new { target = 300 });
        await plain.PostOkAsync($"{U}/{id}/job/quantity", new { completed = 120, note = "first batch" });
        await planner.PostOkAsync($"{U}/{id}/job/quantity", new { completed = 180, notRepaired = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsync($"{U}/{id}/job/quantity", new { completed = -1000 })).StatusCode);
        Assert.True((await plain.PutAsync($"{U}/{id}/job/parts", new[] { new { partNumber = "123abc", description = "Top mount", perAssembly = 1m } })).IsSuccessStatusCode);
        var job = await plain.GetElAsync($"{U}/{id}/job");
        Assert.Equal(300, job.GetProperty("quantityCompleted").GetInt32());
        Assert.Equal(300m, job.GetProperty("parts")[0].GetProperty("total").GetDecimal());
        await plain.PutAsync($"{U}/{id}/job/parts", new[] { new { partNumber = "123abc", description = "Top mount", perAssembly = 1m, quantity = (decimal?)298m } });
        var report = await plain.GetElAsync($"{U}/{id}/job/report");
        Assert.Equal(300, report.GetProperty("quantityTarget").GetInt32());
        Assert.Equal(2, report.GetProperty("quantityNotRepaired").GetInt32());
        Assert.Equal(298m, report.GetProperty("partsTotal").GetDecimal());
        Assert.False(report.TryGetProperty("quantityScrapped", out _));
        var pdf = await plain.GetAsync($"{U}/{id}/job/report/pdf?rate=50&supplier=Made-up%20supplier");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync())[..4]);
        var history = (await (await app.AdminAsync()).GetElAsync($"{U}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString()).ToList();
        Assert.Contains(history, h => h!.Contains("completed +120") && h.Contains("first batch"));
        Assert.Contains(history, h => h!.Contains("Parts replaced on") && h.Contains("123abc"));
        Assert.Contains(history, h => h!.Contains("Printed the job report"));
    }

    [Fact]
    public async Task Tester_files_are_read_with_remembered_columns()
    {
        var (_, id, _, plain) = await JobAsync();
        var csv = "Batch,Unit,Outcome,Leak rate\nB7,1,PASS,0.10\nB7,2,PASS,0.20\nB7,3,FAIL,0.90\nB7,4,ok,0.15\n";
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "files", "made-up-run.csv" },
            { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n")), "files", "made-up-cert.pdf" }, { new StringContent("2026-10-20"), "testDate" } };
        var up = await plain.Http.PostAsync($"{U}/{id}/job/test-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var files = (await up.JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(4, files[0].GetProperty("tested").GetInt32()); // "Outcome" is found as the result column
        Assert.Equal(3, files[0].GetProperty("passed").GetInt32()); // PASS and ok are pass by default
        Assert.Equal(JsonValueKind.Null, files[1].GetProperty("tested").ValueKind);
        var cols = await plain.GetElAsync($"{U}/job/test-files/{files[0].Id()}/columns");
        Assert.Equal("Outcome", cols.GetProperty("suggested").GetString());
        await plain.PutAsync($"{U}/{id}/job/columns", new { resultColumn = "Outcome", passValues = new[] { "PASS" }, valueColumns = new[] { "Leak rate" }, batchColumn = "Batch" });
        var job = await plain.GetElAsync($"{U}/{id}/job");
        var f0 = job.GetProperty("files")[0];
        Assert.Equal(2, f0.GetProperty("passed").GetInt32());
        Assert.Equal("B7", f0.GetProperty("batch").GetString());
        Assert.Equal(0.9m, f0.GetProperty("keyValues")[0].GetProperty("max").GetDecimal());
        Assert.Equal(2, job.GetProperty("tests").GetProperty("passed").GetInt32());
        var dl = await plain.GetAsync($"{U}/job/test-files/{files[0].Id()}");
        Assert.Contains("Leak rate", await dl.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.Http.PostAsync($"{U}/{id}/job/test-files",
            new MultipartFormDataContent { { new ByteArrayContent([1, 2]), "files", "made-up.exe" } })).StatusCode);
    }

    [Fact]
    public async Task Official_status_in_NetSuite_is_informational_filterable_and_audited()
    {
        var (site, id, planner, plain) = await JobAsync();
        var part = await PartAsync(site);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsync($"/api/sequfloh/status-note/part/{part}", new { tag = "Repair hold" })).StatusCode);
        Assert.True((await planner.PutAsync($"/api/sequfloh/status-note/part/{part}", new { tag = "Quarantine hold (NetSuite)", note = "RMA 12" })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PutAsync($"/api/sequfloh/status-note/part/{part}", new { tag = "Made up" })).StatusCode);
        var parts = await planner.GetElAsync($"/api/sequfloh/parts?tag={Uri.EscapeDataString("Quarantine hold (NetSuite)")}&pageSize=200");
        Assert.Contains(parts.GetProperty("items").EnumerateArray(), p => p.Id() == part && p.GetProperty("statusNote").GetString() == "RMA 12");
        Assert.True((await plain.PutAsync($"/api/sequfloh/status-note/work-order/{id}", new { tag = "Repair hold" })).IsSuccessStatusCode);
        var start = await Act(plain, id, "start", await Ver(plain, id)); // never blocks work
        Assert.True(start.IsSuccessStatusCode);
        Assert.False((await plain.GetElAsync($"{U}/{id}/job/report")).TryGetProperty("statusTag", out _)); // not on the chargeback report
        var list = await plain.GetElAsync($"{U}?folder=all&tag=none&pageSize=200");
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), x => x.Id() == id);
        var history = (await (await app.AdminAsync()).GetElAsync($"{U}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(history, h => h.Contains("Repair hold"));
    }

    [Fact]
    public async Task Org_audit_exports_to_csv_only_with_the_audit_permission()
    {
        var (_, _, planner, plain) = await JobAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/sequfloh/audit/system/csv")).StatusCode);
        var csv = await planner.GetAsync("/api/sequfloh/audit/system/csv");
        Assert.True(csv.IsSuccessStatusCode, await csv.Content.ReadAsStringAsync());
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("When (UTC),Who,What", await csv.Content.ReadAsStringAsync());
    }
}
