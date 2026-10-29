using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>8D Report files on work orders, inspection requests and projects, and their links in the job and chargeback reports. Made-up data only.</summary>
[Collection("api")]
public sealed class EightDReportTests(TestApp app)
{
    const string E = "/api/sequfloh/8d";
    static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4];
    static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
    static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n");

    static MultipartFormDataContent Form(byte[] bytes, string name) => new() { { new ByteArrayContent(bytes), "file", name } };

    async Task<(Guid Site, ApiClient Mgr, string Tag)> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks], site);
        return (site, mgr, Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());
    }

    async Task WithDb(Func<AppDbContext, Task> fn) { using var s = app.Services.CreateScope(); await fn(s.ServiceProvider.GetRequiredService<AppDbContext>()); }

    [Fact]
    public async Task Each_allowed_type_uploads_under_its_own_name_and_bad_files_are_refused()
    {
        var (site, mgr, tag) = await SetupAsync();
        var wo = await (await mgr.PostOkAsync("/api/sequfloh/tracking", new { type = "Production", siteId = site, include8D = true })).JsonAsync();
        var id = wo.Id();
        Assert.True((await mgr.GetElAsync($"/api/sequfloh/tracking/{id}")).GetProperty("record").GetProperty("include8D").GetBoolean());
        foreach (var (name, bytes) in new[] { ("Made-up 8D.pdf", Pdf), ("made-up.doc", Ole), ("made-up.docx", Zip), ("made-up.xls", Ole), ("made-up.xlsx", Zip), ("made-up.xlsm", Zip), ("made-up.csv", Encoding.UTF8.GetBytes("D1,Team\nD2,Problem\n")) })
        {
            var r = await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form(bytes, name));
            Assert.True(r.IsSuccessStatusCode, name + ": " + await r.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form([0x4D, 0x5A, 0, 0], "tool.exe"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form([0x4D, 0x5A, 0, 0], "renamed.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form(Pdf, "renamed.docx"))).StatusCode);

        var list = await mgr.GetElAsync($"{E}/work-orders/{id}");
        var files = list.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(7, files.Count);
        var pdf = files.Single(f => f.GetProperty("fileName").GetString() == "Made-up 8D.pdf");
        Assert.False(string.IsNullOrEmpty(pdf.GetProperty("uploadedBy").GetString()));
        var open = await mgr.Http.GetAsync($"{E}/files/{pdf.Id()}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(Pdf, await open.Content.ReadAsByteArrayAsync());
        var dl = await mgr.Http.GetAsync($"{E}/files/{pdf.Id()}?download=true");
        Assert.Equal("Made-up 8D.pdf", dl.Content.Headers.ContentDisposition?.FileNameStar ?? dl.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // someone at another site cannot open it
        var (_, other) = await app.CreateUserAsync([Permissions.SequflohAccess], await app.CreateSiteAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"{E}/files/{pdf.Id()}")).StatusCode);

        // No is refused while files are attached
        var no = await mgr.PutAsync($"{E}/work-orders/{id}/include", new { include = false });
        Assert.Equal(HttpStatusCode.BadRequest, no.StatusCode);
        Assert.Contains("Remove the 8D files first", await no.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await mgr.Http.DeleteAsync($"{E}/files/{pdf.Id()}")).StatusCode);
        Assert.Equal(6, (await mgr.GetElAsync($"{E}/work-orders/{id}")).GetProperty("files").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Http.GetAsync($"{E}/files/{pdf.Id()}")).StatusCode);
        await WithDb(async db =>
        {
            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.Action.StartsWith("EightD")).ToListAsync();
            Assert.Contains(audit, a => a.Action == "EightDUploaded" && a.Description.Contains("Made-up 8D.pdf") && a.UserName.Length > 0);
            Assert.Contains(audit, a => a.Action == "EightDRemoved" && a.Description == $"8D report Made-up 8D.pdf removed from work order {wo.GetProperty("number").GetString()}");
        });
    }

    [Fact]
    public async Task Completed_work_orders_lock_and_validation_fai_has_no_8d()
    {
        var (site, mgr, _) = await SetupAsync();
        var id = (await (await mgr.PostOkAsync("/api/sequfloh/tracking", new { type = "Production", siteId = site })).JsonAsync()).Id();
        Assert.False((await mgr.GetElAsync($"{E}/work-orders/{id}")).GetProperty("include").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"{E}/work-orders/{id}/include", new { include = true })).StatusCode);
        var f = (await (await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form(Pdf, "made-up.pdf"))).JsonAsync()).Id();
        await WithDb(db => db.SequflohTrackingRecords.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, TrackingStatus.Completed)));
        var l = await mgr.GetElAsync($"{E}/work-orders/{id}");
        Assert.False(l.GetProperty("editable").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Http.PostAsync($"{E}/work-orders/{id}", Form(Pdf, "late.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Http.DeleteAsync($"{E}/files/{f}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Http.GetAsync($"{E}/files/{f}")).StatusCode);

        // Validation / FAI: refused at create and on upload
        var bad = await mgr.PostAsync("/api/sequfloh/tracking", new { type = "Production", siteId = site });
        var vid = (await bad.JsonAsync()).Id();
        await WithDb(db => db.SequflohTrackingRecords.Where(r => r.Id == vid).ExecuteUpdateAsync(s => s.SetProperty(r => r.Type, TrackingType.Validation)));
        var up = await mgr.Http.PostAsync($"{E}/work-orders/{vid}", Form(Pdf, "made-up.pdf"));
        Assert.Equal(HttpStatusCode.Conflict, up.StatusCode);
        Assert.Contains("Validation / FAI", await up.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.PutAsync($"{E}/work-orders/{vid}/include", new { include = true })).StatusCode);
    }

    [Fact]
    public async Task Requests_and_projects_take_8d_files_and_the_reports_link_them()
    {
        var (site, mgr, tag) = await SetupAsync();
        // inspection request: Yes at create, upload, No refused, then off once empty
        var req = (await (await mgr.PostOkAsync("/api/sequfloh/inspection-requests", new { siteId = site, partNumber = "MADE-UP-" + tag, include8D = true })).JsonAsync()).Id();
        Assert.True((await mgr.GetElAsync($"/api/sequfloh/inspection-requests/{req}")).GetProperty("include8D").GetBoolean());
        var rf = (await (await mgr.Http.PostAsync($"{E}/inspection-requests/{req}", Form(Zip, "IR 8D.xlsx"))).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.PutAsync($"{E}/inspection-requests/{req}/include", new { include = false })).StatusCode);

        // project: Yes on the form; No refused with files
        var proj = await (await mgr.PostOkAsync("/api/sequfloh/projects", new { name = "Example 8D " + tag, include8D = true })).JsonAsync();
        var pid = proj.Id();
        Assert.True((await mgr.GetElAsync($"/api/sequfloh/projects/{pid}")).GetProperty("include8D").GetBoolean());
        var pf = (await (await mgr.Http.PostAsync($"{E}/projects/{pid}", Form(Pdf, "Project 8D.pdf"))).JsonAsync()).Id();
        var pno = await mgr.PutAsync($"/api/sequfloh/projects/{pid}", new { number = proj.GetProperty("number").GetString(), name = "Example 8D " + tag, include8D = false });
        Assert.Equal(HttpStatusCode.BadRequest, pno.StatusCode);

        // a work order with an 8D, linked with the request to the project
        var wo = await (await mgr.PostOkAsync("/api/sequfloh/tracking", new { type = "Production", siteId = site, include8D = true, projectId = pid })).JsonAsync();
        var wf = (await (await mgr.Http.PostAsync($"{E}/work-orders/{wo.Id()}", Form(Ole, "WO 8D.doc"))).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"/api/sequfloh/projects/inspection-requests/{req}", new { projectId = pid })).StatusCode);

        // job report
        var job = await mgr.GetElAsync($"/api/sequfloh/tracking/{wo.Id()}/job/report");
        Assert.Equal([wf], job.GetProperty("eightD").EnumerateArray().Select(x => x.Id()).ToList());

        // work order chargeback: the 8D block with a link per file
        var cb = (await (await mgr.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = wo.GetProperty("number").GetString(), trackingRecordId = wo.Id() })).JsonAsync()).Id();
        var block = (await mgr.GetElAsync($"/api/sequfloh/chargebacks/{cb}/view")).EnumerateArray().Single(b => b.GetProperty("key").GetString() == "eightD");
        Assert.Equal("8D Report", block.GetProperty("heading").GetString());
        Assert.Equal([wf], block.GetProperty("links").EnumerateArray().Select(x => x.Id()).ToList());
        var x = await mgr.Http.GetAsync($"/api/sequfloh/chargebacks/{cb}/xlsx");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);

        // project chargeback rollup: the project's own and its linked records' files
        var pcb = (await (await mgr.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = "", period = new { kind = "Project", projectId = pid }, sections = new[] { "sumHours" } })).JsonAsync()).Id();
        var pblock = (await mgr.GetElAsync($"/api/sequfloh/chargebacks/{pcb}/view")).EnumerateArray().Single(b => b.GetProperty("key").GetString() == "eightD");
        Assert.Equal(new[] { pf, wf, rf }.Order().ToList(), pblock.GetProperty("links").EnumerateArray().Select(x => x.Id()).Order().ToList());
        Assert.Equal(HttpStatusCode.OK, (await mgr.Http.GetAsync($"/api/sequfloh/chargebacks/{pcb}/pdf")).StatusCode);

        // a request emptied of 8D files can be turned off
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.Http.DeleteAsync($"{E}/files/{rf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"{E}/inspection-requests/{req}/include", new { include = false })).StatusCode);
        Assert.False((await mgr.GetElAsync($"{E}/inspection-requests/{req}")).GetProperty("include").GetBoolean());
        await WithDb(async db => Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == req).ToListAsync(), a => a.Action == "EightDIncluded" && a.Description.Contains("turned off")));
    }
}
