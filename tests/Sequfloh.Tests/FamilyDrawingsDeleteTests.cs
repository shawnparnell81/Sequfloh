using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
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
}
