using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Delete all drawings of one family (a top-level drawings folder and the folders under it). Made-up drawings only.</summary>
[Collection("api")]
public sealed class FamilyDrawingsDeleteTests(TestApp app)
{
    const string S = "/api/sequfloh";
    const string D = "/api/drawings";

    AppDbContext Db(out IServiceScope scope) { scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<AppDbContext>(); }

    async Task<Guid> FolderAsync(ApiClient c, string name, Guid site, Guid? parent = null) =>
        (await (await c.PostOkAsync($"{D}/folders", new { name, siteId = site, parentId = parent })).JsonAsync()).Id();

    async Task<Guid> DrawingAsync(ApiClient c, Guid folder, string number, string title, Guid? partId = null)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% family delete drawing " + number + "\n%%EOF\n")), "file", number + ".pdf" },
            { new StringContent(title), "title" }, { new StringContent(number), "drawingNumber" }, { new StringContent("A"), "revision" },
        };
        if (partId is Guid part) form.Add(new StringContent(part.ToString()), "partIds");
        var res = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", form);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).Id();
    }

    async Task<Guid> PartAsync(Guid site, string pn)
    {
        var db = Db(out var s); using var _ = s;
        var p = new Part { PartNumber = pn, Name = "Example part " + pn, Revision = "A", UnitOfMeasure = "EA", SiteId = site, Status = MasterStatus.Active };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    static string Url(Guid family) => $"{D}/families/{family}";

    [Fact]
    public async Task Free_drawings_in_one_family_are_deleted_and_other_families_stay()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FolderAsync(admin, "Wipe family " + tag + " Drawings", site);
        var sub = await FolderAsync(admin, "PX-" + tag, site, fam);
        var other = await FolderAsync(admin, "Keep family " + tag + " Drawings", site);
        var part = await PartAsync(site, "GD-" + tag);
        var linked = await DrawingAsync(admin, fam, "WA-" + tag, "Spacer " + tag, part);
        var nested = await DrawingAsync(admin, sub, "WB-" + tag, "Nested " + tag);
        var archived = await DrawingAsync(admin, fam, "WC-" + tag, "Old " + tag);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{D}/{archived}/archive", new { archived = true })).StatusCode);
        var kept = await DrawingAsync(admin, other, "KA-" + tag, "Keep " + tag);
        var order = (await (await admin.PostOkAsync($"{S}/tracking", new { type = "Production", siteId = site, partId = part })).JsonAsync()).Id();
        Assert.Contains((await admin.GetElAsync($"{D}/for-record/{order}")).EnumerateArray(), d => d.Id() == linked);

        var home = await admin.GetElAsync($"{D}/folders");
        var card = home.GetProperty("folders").EnumerateArray().Single(f => f.Str("name") == "Wipe family " + tag + " Drawings");
        Assert.Equal(3, card.Int("treeDrawings"));
        Assert.Equal(1, home.GetProperty("folders").EnumerateArray().Single(f => f.Str("name") == "Keep family " + tag + " Drawings").Int("treeDrawings"));

        var plan = await admin.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal("Wipe family " + tag, plan.Str("family"));
        Assert.Equal(3, plan.Int("total"));
        Assert.Equal(3, plan.Int("deletable"));
        Assert.Equal(0, plan.Int("skipped"));

        string[] keys;
        { var db = Db(out var scope); using var _ = scope; keys = await db.DrawingRevisions.Where(r => r.DrawingId == linked || r.DrawingId == nested || r.DrawingId == archived).Select(r => r.StorageKey).ToArrayAsync(); }
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));

        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(3, done.Int("deleted"));
        Assert.Equal(0, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{D}/{linked}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/tracking/{order}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/parts/{part}")).StatusCode);
        Assert.Empty((await admin.GetElAsync($"{D}/for-record/{order}")).EnumerateArray());

        var dbAfter = Db(out var scopeAfter); using var end = scopeAfter;
        Assert.False(await dbAfter.Drawings.AnyAsync(d => d.Id == linked || d.Id == nested || d.Id == archived));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == kept));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == fam));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == sub));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == other));
        Assert.False(await dbAfter.DrawingPartLinks.AnyAsync(l => l.DrawingId == linked || l.PartId == part));
        Assert.False(await dbAfter.TrackingRecordDrawings.AnyAsync(l => l.DrawingId == linked || l.TrackingRecordId == order));
        Assert.All(keys, k => Assert.False(File.Exists(Path.Combine(TestApp.FilesPath, k))));
        var audits = await dbAfter.AuditEntries.Where(a => (a.EntityId == linked || a.EntityId == nested || a.EntityId == archived) && a.Action == "Deleted").ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.UserName));
            Assert.Equal("Drawing", a.EntityType);
            Assert.Contains("Deleted drawing", a.Description);
            Assert.True(a.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
        var linkedAudit = audits.Single(a => a.EntityId == linked);
        Assert.Contains("unlinked from 1 part", linkedAudit.Description);
        Assert.Contains("removed from 1 work order", linkedAudit.Description);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{D}/families/{sub}/delete")).StatusCode);
    }

    [Fact]
    public async Task A_drawing_the_person_cannot_delete_is_skipped()
    {
        var site = await app.CreateSiteAsync();
        var otherSite = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FolderAsync(admin, "Locked family " + tag + " Drawings", site);
        var free = await DrawingAsync(admin, fam, "UF-" + tag, "Free " + tag);
        var held = await DrawingAsync(admin, fam, "UH-" + tag, "Held " + tag);
        { var db = Db(out var s); using var hold = s; var row = await db.Drawings.SingleAsync(d => d.Id == held); row.SiteId = otherSite; await db.SaveChangesAsync(); }

        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohDeleteRecords, Permissions.SequflohViewDrawings], site);
        var plan = await user.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal(2, plan.Int("total"));
        Assert.Equal(1, plan.Int("deletable"));
        Assert.Equal(1, plan.Int("skipped"));
        var skip = plan.GetProperty("skips").EnumerateArray().Single();
        Assert.Equal("UH-" + tag, skip.Str("drawingNumber"));
        Assert.Equal("You do not have permission to delete this drawing.", skip.Str("reason"));

        var done = await (await user.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(1, done.Int("deleted"));
        Assert.Equal(1, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal("UH-" + tag, done.GetProperty("skips").EnumerateArray().Single().Str("drawingNumber"));

        var dbAfter = Db(out var scope); using var _ = scope;
        Assert.False(await dbAfter.Drawings.AnyAsync(d => d.Id == free));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == held));
        Assert.False(await dbAfter.AuditEntries.AnyAsync(a => a.EntityId == held && a.Action == "Deleted"));
    }

    [Fact]
    public async Task Empty_family_deletes_nothing()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FolderAsync(admin, "Empty family " + tag, site);
        var home = await admin.GetElAsync($"{D}/folders");
        Assert.Equal(0, home.GetProperty("folders").EnumerateArray().Single(f => f.Id() == fam).Int("treeDrawings"));
        var plan = await admin.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal("Empty family " + tag, plan.Str("family"));
        Assert.Equal(0, plan.Int("total"));
        Assert.Equal(0, plan.Int("deletable"));
        Assert.Equal(0, plan.Int("skipped"));
        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(0, done.Int("deleted"));
        Assert.Equal(0, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{D}/families/{Guid.NewGuid()}/delete")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{D}/families/none/delete")).StatusCode);
        var db = Db(out var scope); using var _ = scope;
        Assert.True(await db.DrawingFolders.AnyAsync(f => f.Id == fam));
    }

    [Fact]
    public async Task Delete_family_needs_the_delete_records_permission()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FolderAsync(admin, "No delete " + tag + " Drawings", site);
        var drawing = await DrawingAsync(admin, fam, "PL-" + tag, "Locked " + tag);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings], site);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"{Url(fam)}/delete-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{Url(fam)}/delete")).StatusCode);
        var db = Db(out var scope); using var _ = scope;
        Assert.True(await db.Drawings.AnyAsync(d => d.Id == drawing));
    }

    /// <summary>Enough drawings to cross more than one delete batch, including a revision, a part link, a work order link, an archived drawing and one the person cannot delete.</summary>
    [Fact]
    public async Task A_large_family_is_deleted_in_one_call_and_skips_are_kept()
    {
        const int bulk = 220;
        var site = await app.CreateSiteAsync();
        var otherSite = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FolderAsync(admin, "Bulk family " + tag + " Drawings", site);
        var other = await FolderAsync(admin, "Other family " + tag + " Drawings", site);
        var part = await PartAsync(site, "GD-" + tag);
        var order = (await (await admin.PostOkAsync($"{S}/tracking", new { type = "Production", siteId = site, partId = part })).JsonAsync()).Id();
        var kept = await DrawingAsync(admin, other, "KA-" + tag, "Keep " + tag);

        var ids = new Guid[bulk];
        var keys = new List<string>();
        Guid heldId;
        string heldKey;
        string twoNumber;
        {
            var db = Db(out var scope); using var hold = scope;
            var adminId = await db.Users.Where(u => u.Email == TestApp.AdminEmail).Select(u => u.Id).FirstAsync();
            Guid linkedRev = Guid.Empty;
            for (var i = 0; i < bulk; i++)
            {
                var number = $"B{i:000}-" + tag;
                var drawing = new Drawing
                {
                    FolderId = fam, Title = "Bulk " + i + " " + tag, DrawingNumber = number, CurrentRevision = i == 1 ? "B" : "A", SiteId = site, IsArchived = i == 2,
                };
                var rev = Revision(drawing.Id, "A", number, i == 1 ? DrawingRevisionStatus.Superseded : DrawingRevisionStatus.Current);
                db.Drawings.Add(drawing);
                db.DrawingRevisions.Add(rev);
                ids[i] = drawing.Id;
                keys.Add(rev.StorageKey);
                if (i == 0) linkedRev = rev.Id;
                if (i == 1)
                {
                    var second = Revision(drawing.Id, "B", number, DrawingRevisionStatus.Current);
                    db.DrawingRevisions.Add(second);
                    keys.Add(second.StorageKey);
                }
            }
            twoNumber = $"B001-" + tag;
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = ids[0], PartId = part, CreatedAt = DateTimeOffset.UtcNow });
            db.TrackingRecordDrawings.Add(new TrackingRecordDrawing
            {
                TrackingRecordId = order, DrawingId = ids[0], AttachedRevision = "A", AttachedDrawingNumber = $"B000-" + tag, AttachedRevisionId = linkedRev, AttachedAt = DateTimeOffset.UtcNow,
            });
            db.SequflohUserItems.Add(new SequflohUserItem
            {
                UserId = adminId, Kind = "recent", ItemType = "drawing", RecordId = ids[3], Path = "/sequfloh/drawings/" + ids[3], Label = "Bulk recent", LastOpenedAt = DateTimeOffset.UtcNow,
            });
            var held = new Drawing { FolderId = fam, Title = "Held " + tag, DrawingNumber = "UH-" + tag, CurrentRevision = "A", SiteId = otherSite };
            var heldRev = Revision(held.Id, "A", held.DrawingNumber!, DrawingRevisionStatus.Current);
            db.Drawings.Add(held);
            db.DrawingRevisions.Add(heldRev);
            heldId = held.Id;
            heldKey = heldRev.StorageKey;
            await db.SaveChangesAsync();
        }
        foreach (var key in keys) WriteFile(key);
        WriteFile(heldKey);
        string keptKey;
        { var db = Db(out var scope); using var read = scope; keptKey = await db.DrawingRevisions.Where(r => r.DrawingId == kept).Select(r => r.StorageKey).SingleAsync(); }
        Assert.All(keys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));
        Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, heldKey)));

        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohDeleteRecords, Permissions.SequflohViewDrawings], site);
        var plan = await user.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal("Bulk family " + tag, plan.Str("family"));
        Assert.Equal(bulk + 1, plan.Int("total"));
        Assert.Equal(bulk, plan.Int("deletable"));
        Assert.Equal(1, plan.Int("skipped"));
        var plannedSkip = plan.GetProperty("skips").EnumerateArray().Single();
        Assert.Equal("UH-" + tag, plannedSkip.Str("drawingNumber"));
        Assert.Equal("You do not have permission to delete this drawing.", plannedSkip.Str("reason"));

        var calls = 0;
        var deleted = 0;
        JsonElement done = default;
        do
        {
            calls++;
            done = await (await user.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
            deleted += done.Int("deleted");
        } while (done.GetProperty("more").GetBoolean() && calls < 5);
        Assert.Equal(1, calls);
        Assert.Equal(bulk, deleted);
        Assert.Equal(1, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        var skip = done.GetProperty("skips").EnumerateArray().Single();
        Assert.Equal("UH-" + tag, skip.Str("drawingNumber"));
        Assert.Equal("You do not have permission to delete this drawing.", skip.Str("reason"));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{D}/{ids[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{D}/{heldId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{D}/{kept}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/tracking/{order}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/parts/{part}")).StatusCode);
        Assert.All(keys, k => Assert.False(File.Exists(Path.Combine(TestApp.FilesPath, k))));
        Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, heldKey)));
        Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, keptKey)));

        var dbAfter = Db(out var scopeAfter); using var end = scopeAfter;
        Assert.False(await dbAfter.Drawings.AnyAsync(d => ids.Contains(d.Id)));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == heldId));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == kept));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == fam));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == other));
        Assert.False(await dbAfter.DrawingPartLinks.AnyAsync(l => l.DrawingId == ids[0] || l.PartId == part));
        Assert.False(await dbAfter.TrackingRecordDrawings.AnyAsync(l => l.DrawingId == ids[0] || l.TrackingRecordId == order));
        Assert.False(await dbAfter.DrawingRevisions.AnyAsync(r => ids.Contains(r.DrawingId)));
        Assert.False(await dbAfter.SequflohUserItems.AnyAsync(i => i.RecordId == ids[3]));
        var audits = await dbAfter.AuditEntries.Where(a => ids.Contains(a.EntityId) && a.Action == "Deleted").ToListAsync();
        Assert.Equal(bulk, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.UserName));
            Assert.Equal("Drawing", a.EntityType);
            Assert.Contains("Deleted drawing", a.Description);
            Assert.True(a.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
        var linkedAudit = audits.Single(a => a.EntityId == ids[0]);
        Assert.Contains("with 1 revision", linkedAudit.Description);
        Assert.Contains("unlinked from 1 part", linkedAudit.Description);
        Assert.Contains("removed from 1 work order", linkedAudit.Description);
        Assert.Contains("with 2 revisions", audits.Single(a => a.EntityId == ids[1]).Description);
        Assert.Contains(twoNumber, audits.Single(a => a.EntityId == ids[1]).Description);
        var plain = audits.Single(a => a.EntityId == ids[4]);
        Assert.Contains("with 1 revision", plain.Description);
        Assert.DoesNotContain("unlinked", plain.Description);
        Assert.DoesNotContain("work order", plain.Description);
        Assert.False(await dbAfter.AuditEntries.AnyAsync(a => a.EntityId == heldId && a.Action == "Deleted"));
        Assert.False(await dbAfter.AuditEntries.AnyAsync(a => a.EntityId == kept && a.Action == "Deleted"));
    }

    static DrawingRevision Revision(Guid drawingId, string revision, string number, DrawingRevisionStatus status) => new()
    {
        DrawingId = drawingId, Revision = revision, DrawingNumber = number, FileName = number + "-" + revision + ".pdf", ContentType = "application/pdf",
        StorageKey = $"drawings/{drawingId:N}/{Guid.NewGuid():N}.pdf", SizeBytes = 20, Sha256 = new string(revision[0], 64), Status = status, CreatedByName = "Test",
    };

    static void WriteFile(string key)
    {
        var path = Path.Combine(TestApp.FilesPath, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.4\n"));
    }
}
