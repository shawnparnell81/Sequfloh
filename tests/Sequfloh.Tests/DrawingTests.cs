using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class DrawingTests(TestApp app)
{
    const string D = "/api/drawings";
    static readonly string[] Manager = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];
    static readonly string[] Viewer = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings];

    /// <summary>A tiny made-up PDF (only the signature matters to the upload check).</summary>
    static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {text}\n%%EOF\n");

    static MultipartFormDataContent Form(byte[] bytes, string fileName, params (string Key, string Value)[] fields)
    {
        var f = new MultipartFormDataContent();
        f.Add(new ByteArrayContent(bytes), "file", fileName);
        foreach (var (k, v) in fields) f.Add(new StringContent(v), k);
        return f;
    }

    async Task<(Guid Id, string Number)> PartAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = new Part { PartNumber = "DP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Test bracket", SiteId = site, Status = MasterStatus.Active };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return (p.Id, p.PartNumber);
    }

    static async Task<Guid> FolderAsync(ApiClient c, Guid site, string name, Guid? parentId = null, Guid? partId = null) =>
        (await (await c.PostOkAsync($"{D}/folders", new { name, parentId, partId, siteId = site })).JsonAsync()).Id();

    static async Task<Guid> UploadAsync(ApiClient c, Guid folder, string title, string number, string rev, string? partIds = null)
    {
        var fields = new List<(string, string)> { ("title", title), ("drawingNumber", number), ("revision", rev) };
        if (partIds is not null) fields.Add(("partIds", partIds));
        var r = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(Pdf(title), "bracket.pdf", [.. fields]));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.JsonAsync()).Id();
    }

    [Fact]
    public async Task Folders_drawings_and_revisions_keep_history_and_are_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, mgr) = await app.CreateUserAsync(Manager, site);
        var (_, viewer) = await app.CreateUserAsync(Viewer, site);
        var (_, outsider) = await app.CreateUserAsync([Permissions.SequflohAccess], site);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{D}/folders", new { name = "X", siteId = site })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{D}/folders")).StatusCode);
        var top = await FolderAsync(mgr, site, "Brackets");
        var sub = await FolderAsync(mgr, site, "Machined", top);
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.PostAsync($"{D}/folders", new { name = "Machined", parentId = top })).StatusCode);
        Assert.True((await mgr.PutAsync($"{D}/folders/{sub}", new { name = "Machined parts", parentId = top })).IsSuccessStatusCode);

        var id = await UploadAsync(mgr, sub, "Mounting bracket", "DWG-100", "A");
        var bad = await mgr.Http.PostAsync($"{D}/folders/{sub}/drawings", Form(Encoding.ASCII.GetBytes("not a pdf"), "fake.pdf", ("title", "x"), ("revision", "A")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var exe = await mgr.Http.PostAsync($"{D}/folders/{sub}/drawings", Form(Pdf("x"), "tool.exe", ("title", "x"), ("revision", "A")));
        Assert.Equal(HttpStatusCode.BadRequest, exe.StatusCode);

        var dup = await mgr.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("again"), "bracket.pdf", ("revision", "A")));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.True((await mgr.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("rev b"), "bracket-b.pdf", ("revision", "B"), ("notes", "Hole moved")))).IsSuccessStatusCode);

        var d = await viewer.GetElAsync($"{D}/{id}");
        Assert.Equal("B", d.Str("currentRevision"));
        var revs = d.GetProperty("revisions").EnumerateArray().Select(r => r.Str("revision")).ToList();
        Assert.Equal(["B", "A"], revs);

        var inline = await viewer.GetAsync($"{D}/{id}/file?view=true");
        Assert.Equal("application/pdf", inline.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", inline.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("rev b", await inline.Content.ReadAsStringAsync());
        var old = await viewer.GetAsync($"{D}/{id}/file?revision=A");
        Assert.Equal("attachment", old.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Mounting bracket", await old.Content.ReadAsStringAsync());

        var found = await viewer.GetElAsync($"{D}/search?q=DWG-100");
        Assert.Contains(found.EnumerateArray(), x => x.Id() == id);

        Assert.True((await mgr.PostAsync($"{D}/{id}/archive", new { archived = true })).IsSuccessStatusCode);
        Assert.True((await mgr.PostAsync($"{D}/folders/{top}/archive", new { archived = true })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.PostAsync($"{D}/folders/{sub}/archive", new { archived = false })).StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.DrawingRevisions.CountAsync(v => v.DrawingId == id));
        var audit = await db.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action + ": " + a.Description).ToListAsync();
        Assert.Contains(audit, a => a.Contains("revision B") && a.Contains("revision A is kept"));
        Assert.All(await db.AuditEntries.Where(a => a.EntityId == id).ToListAsync(), a => Assert.False(string.IsNullOrEmpty(a.UserName)));
    }

    [Fact]
    public async Task Drawings_link_to_parts_from_either_side_and_are_found_by_part_number()
    {
        var site = await app.CreateSiteAsync();
        var (_, mgr) = await app.CreateUserAsync(Manager, site);
        var (_, tech) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (partId, number) = await PartAsync(site);
        var (otherId, _) = await PartAsync(site);
        var folder = await FolderAsync(mgr, site, "Loose");

        // from the drawing side: linked while uploading
        var a = await UploadAsync(mgr, folder, "Bracket assembly", "DWG-200", "C", $"{partId},{otherId}");
        // from the part side
        var b = await UploadAsync(mgr, folder, "Bracket detail", "DWG-201", "A");
        Assert.True((await mgr.PutAsync($"{D}/for-part/{partId}", new { ids = new[] { a, b } })).IsSuccessStatusCode);
        // straight from the part page, into a folder made for the part
        var up = await mgr.Http.PostAsync($"{D}/for-part/{partId}/upload", Form(Pdf("p"), "plate.pdf", ("title", "Base plate"), ("revision", "A")));
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var c = (await up.JsonAsync()).Id();

        var list = await mgr.GetElAsync($"{D}/for-part?partNumber={number.ToLowerInvariant()}");
        Assert.Equal(3, list.GetArrayLength());
        Assert.Contains(list.EnumerateArray(), x => x.Id() == a && x.Str("currentRevision") == "C" && x.GetProperty("viewable").GetBoolean());
        Assert.Contains((await mgr.GetElAsync($"{D}/search?q={number}")).EnumerateArray(), x => x.Id() == b);
        var detail = await mgr.GetElAsync($"{D}/{a}");
        Assert.Equal(2, detail.GetProperty("parts").GetArrayLength());
        Assert.True((await mgr.PutAsync($"{D}/{a}/parts", new { ids = new[] { partId } })).IsSuccessStatusCode);
        Assert.Equal(1, (await mgr.GetElAsync($"{D}/{a}")).GetProperty("parts").GetArrayLength());
        var folders = await mgr.GetElAsync($"{D}/folders");
        Assert.Contains(folders.GetProperty("folders").EnumerateArray(), f => f.Str("name") == number);
        _ = c;

        // unknown part numbers and users without drawing access quietly get nothing
        Assert.Equal(0, (await mgr.GetElAsync($"{D}/for-part?partNumber=NOPE-123")).GetArrayLength());
        Assert.Equal(0, (await tech.GetElAsync($"{D}/for-part?partNumber={number}")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PutAsync($"{D}/for-part/{partId}", new { ids = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Validation_work_orders_attach_the_parts_drawings_and_show_the_current_revision()
    {
        var site = await app.CreateSiteAsync();
        var (_, mgr) = await app.CreateUserAsync(Manager, site);
        var (_, planner) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateFaiRecord], site);
        var (partId, _) = await PartAsync(site);
        var folder = await FolderAsync(mgr, site, "Orders");
        var dwg = await UploadAsync(mgr, folder, "Housing", "DWG-300", "A", partId.ToString());

        Guid wi, wiFai;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SequflohWorkInstruction W(WorkInstructionType t) { var w = new SequflohWorkInstruction { Type = t, Title = "Check", Number = "TWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
                Status = WorkInstructionStatus.Released, Steps = "[]", Characteristics = "[]", PartsRows = "[]", Content = "{}" }; w.FamilyId = w.Id; db.SequflohWorkInstructions.Add(w); return w; }
            wi = W(WorkInstructionType.Validation).Id; wiFai = W(WorkInstructionType.Fai).Id;
            await db.SaveChangesAsync();
        }
        var rec = (await (await planner.PostOkAsync("/api/sequfloh/tracking", new { type = "Validation", workInstructionId = wi, siteId = site, partId })).JsonAsync()).Id();
        var fai = (await (await planner.PostOkAsync("/api/sequfloh/tracking", new { type = "Fai", workInstructionId = wiFai, siteId = site, partId })).JsonAsync()).Id();

        Assert.True((await mgr.Http.PostAsync($"{D}/{dwg}/revisions", Form(Pdf("b"), "housing-b.pdf", ("revision", "B")))).IsSuccessStatusCode);
        var att = await planner.GetElAsync($"{D}/for-record/{rec}");
        var one = Assert.Single(att.EnumerateArray());
        Assert.Equal("A", one.Str("attachedRevision"));
        Assert.Equal("B", one.Str("currentRevision"));
        Assert.Single((await planner.GetElAsync($"{D}/for-record/{fai}")).EnumerateArray()); // every work order type gets the exact part's drawing
    }

    [Fact]
    public async Task A_filled_copy_keeps_the_part_number_it_was_filled_out_for()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,\n")), "file", "made-up checks.csv");
        form.Add(new StringContent(site.ToString()), "siteId");
        form.Add(new StringContent("true"), "blankForm");
        var up = await editor.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var blankId = (await up.JsonAsync()).Id();
        var filled = (await (await editor.PostOkAsync($"/api/imported-files/{blankId}/fill-out", new { title = "Line 2", partNumber = " DP-XYZ " })).JsonAsync()).Id();
        var fd = await editor.GetElAsync($"/api/imported-files/{filled}");
        Assert.Equal("DP-XYZ", fd.Str("partNumber"));
        Assert.True((await editor.PutAsync($"/api/imported-files/{filled}/part-number", new { partNumber = "DP-ABC" })).IsSuccessStatusCode);
        Assert.Equal("DP-ABC", (await editor.GetElAsync($"/api/imported-files/{filled}")).Str("partNumber"));
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PutAsync($"/api/imported-files/{blankId}/part-number", new { partNumber = "DP-ABC" })).StatusCode);
        var blank = await editor.GetElAsync($"/api/imported-files/{blankId}");
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("partNumber").ValueKind);
    }
}
