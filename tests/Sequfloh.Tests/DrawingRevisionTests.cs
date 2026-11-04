using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class DrawingRevisionTests(TestApp app)
{
    const string D = "/api/drawings";
    static readonly string[] Manage = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];
    static readonly string[] Revise = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings, Permissions.SequflohReviseDrawings];
    static readonly string[] Release = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohReleaseDrawings];

    static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {text}\n%%EOF\n");

    static MultipartFormDataContent Form(byte[] bytes, string fileName, params (string Key, string Value)[] fields)
    {
        var f = new MultipartFormDataContent();
        f.Add(new ByteArrayContent(bytes), "file", fileName);
        foreach (var (k, v) in fields) f.Add(new StringContent(v), k);
        return f;
    }

    static async Task<Guid> FolderAsync(ApiClient c, Guid site) =>
        (await (await c.PostOkAsync($"{D}/folders", new { name = "Rev " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();

    static async Task<Guid> DrawingAsync(ApiClient c, Guid folder, string rev = "A")
    {
        var r = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(Pdf("first " + rev), "plate.pdf", ("title", "Plate"), ("drawingNumber", "DWG-REV"), ("revision", rev)));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.JsonAsync()).Id();
    }

    [Fact]
    public async Task Uploading_a_revision_needs_permission_keeps_the_old_file_and_can_be_filtered()
    {
        var site = await app.CreateSiteAsync();
        var (_, manage) = await app.CreateUserAsync(Manage, site);
        var (_, revise) = await app.CreateUserAsync(Revise, site);
        var folder = await FolderAsync(manage, site);
        var id = await DrawingAsync(manage, folder);

        var denied = await manage.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("nope"), "plate-b.pdf", ("revision", "B"), ("reason", "Not allowed")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var missing = await revise.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("nope"), "plate-b.pdf", ("revision", "B")));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("reason", await missing.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        string keyA;
        using (var scope = app.Services.CreateScope())
            keyA = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DrawingRevisions.Where(v => v.DrawingId == id).Select(v => v.StorageKey).SingleAsync();

        var up = await revise.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("second"), "plate-b.pdf", ("revision", "B"), ("reason", "Hole moved"), ("changeReference", "ECN-19")));
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var detail = await revise.GetElAsync($"{D}/{id}");
        Assert.Equal("B", detail.Str("currentRevision"));
        Assert.False(detail.GetProperty("approvalRequired").GetBoolean());
        var rows = detail.GetProperty("revisions").EnumerateArray().ToList();
        Assert.Equal("Current", rows.Single(r => r.Str("revision") == "B").Str("status"));
        Assert.Equal("Hole moved", rows.Single(r => r.Str("revision") == "B").Str("reason"));
        Assert.Equal("ECN-19", rows.Single(r => r.Str("revision") == "B").Str("changeReference"));
        Assert.False(string.IsNullOrEmpty(rows.Single(r => r.Str("revision") == "B").Str("uploadedBy")));
        Assert.Equal("Superseded", rows.Single(r => r.Str("revision") == "A").Str("status"));

        var currentFile = await revise.GetAsync($"{D}/{id}/file");
        Assert.Contains("second", await currentFile.Content.ReadAsStringAsync());
        var oldFile = await revise.GetAsync($"{D}/{id}/file?revision=A");
        Assert.Contains("first A", await oldFile.Content.ReadAsStringAsync());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var keys = await db.DrawingRevisions.Where(v => v.DrawingId == id).Select(v => new { v.Revision, v.StorageKey }).ToListAsync();
            Assert.Equal(2, keys.Count);
            Assert.Equal(keyA, keys.Single(k => k.Revision == "A").StorageKey);
            Assert.NotEqual(keys[0].StorageKey, keys[1].StorageKey);
            Assert.All(keys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k.StorageKey))));
            var audit = await db.AuditEntries.Where(a => a.EntityId == id && a.Action == "Uploaded revision").OrderByDescending(a => a.Id).FirstAsync();
            Assert.False(string.IsNullOrEmpty(audit.UserName));
            Assert.Contains("revision B", audit.Description);
            Assert.Contains("revision A is kept", audit.Description);
            Assert.Contains("Hole moved", audit.Description);
            Assert.Contains("ECN-19", audit.Description);
        }

        Assert.Contains((await revise.GetElAsync($"{D}/search?q=DWG-REV&revisionStatus=Current")).EnumerateArray(), x => x.Id() == id);
        Assert.Contains((await revise.GetElAsync($"{D}/search?revisionStatus=Superseded")).EnumerateArray(), x => x.Id() == id);
        Assert.DoesNotContain((await revise.GetElAsync($"{D}/search?revisionStatus=Pending")).EnumerateArray(), x => x.Id() == id);
        Assert.Contains((await revise.GetElAsync($"{D}/folders?parentId={folder}&revisionStatus=Current")).GetProperty("drawings").EnumerateArray(), x => x.Id() == id);
        Assert.Equal(HttpStatusCode.BadRequest, (await revise.GetAsync($"{D}/search?q=DWG-REV&revisionStatus=Nope")).StatusCode);
    }

    [Fact]
    public async Task With_approval_on_a_revision_stays_pending_until_someone_releases_it()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, revise) = await app.CreateUserAsync(Revise, site);
        var (_, release) = await app.CreateUserAsync(Release, site);
        var folder = await FolderAsync(revise, site);
        var id = await DrawingAsync(revise, folder);
        try
        {
            Assert.True((await admin.PutAsync("/api/sequfloh/settings/numbering", new { value = new { revisionFormat = "Letters", drawingApprovalRequired = true }, rowVersion = 0 })).IsSuccessStatusCode);

            var up = await revise.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("pending b"), "plate-b.pdf", ("revision", "B"), ("reason", "Awaiting release"), ("changeReference", "ECN-20")));
            Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
            var detail = await revise.GetElAsync($"{D}/{id}");
            Assert.Equal("A", detail.Str("currentRevision"));
            Assert.True(detail.GetProperty("approvalRequired").GetBoolean());
            Assert.Equal("Pending", detail.GetProperty("revisions").EnumerateArray().Single(r => r.Str("revision") == "B").Str("status"));
            Assert.Equal("Current", detail.GetProperty("revisions").EnumerateArray().Single(r => r.Str("revision") == "A").Str("status"));
            Assert.Contains("first A", await (await revise.GetAsync($"{D}/{id}/file")).Content.ReadAsStringAsync());
            Assert.Contains("pending b", await (await revise.GetAsync($"{D}/{id}/file?revision=B")).Content.ReadAsStringAsync());

            var again = await revise.Http.PostAsync($"{D}/{id}/revisions", Form(Pdf("another"), "plate-c.pdf", ("revision", "C"), ("reason", "Too soon")));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await revise.PostAsync($"{D}/{id}/revisions/B/release")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await release.PostAsync($"{D}/{id}/revisions/A/release")).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await release.PostAsync($"{D}/{id}/revisions/B/release")).StatusCode);
            var released = await release.GetElAsync($"{D}/{id}");
            Assert.Equal("B", released.Str("currentRevision"));
            Assert.Equal("Current", released.GetProperty("revisions").EnumerateArray().Single(r => r.Str("revision") == "B").Str("status"));
            Assert.Equal("Superseded", released.GetProperty("revisions").EnumerateArray().Single(r => r.Str("revision") == "A").Str("status"));
            Assert.Contains("pending b", await (await release.GetAsync($"{D}/{id}/file")).Content.ReadAsStringAsync());
            Assert.Contains("first A", await (await release.GetAsync($"{D}/{id}/file?revision=A")).Content.ReadAsStringAsync());
            Assert.DoesNotContain((await revise.GetElAsync($"{D}/search?revisionStatus=Pending")).EnumerateArray(), x => x.Id() == id);

            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action + ": " + a.Description).ToListAsync();
            Assert.Contains(audit, a => a.StartsWith("Submitted revision:") && a.Contains("Awaiting release") && a.Contains("ECN-20") && a.Contains("stays current"));
            Assert.Contains(audit, a => a.StartsWith("Released revision:") && a.Contains("revision B") && a.Contains("superseded") && a.Contains("kept"));
            Assert.Equal(2, await db.DrawingRevisions.CountAsync(v => v.DrawingId == id));
            Assert.All(await db.AuditEntries.Where(a => a.EntityId == id && (a.Action == "Submitted revision" || a.Action == "Released revision")).ToListAsync(), a => Assert.False(string.IsNullOrEmpty(a.UserName)));
        }
        finally
        {
            await admin.PutAsync("/api/sequfloh/settings/numbering", new { value = new { revisionFormat = "Letters", drawingApprovalRequired = false }, rowVersion = 0 });
        }
    }
}
