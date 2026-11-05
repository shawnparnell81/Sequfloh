using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Delete all parts of one product family. Parts a work order, instruction or parts list uses are kept. Made-up parts only.</summary>
[Collection("api")]
public sealed class FamilyPartsDeleteTests(TestApp app)
{
    const string S = "/api/sequfloh";

    AppDbContext Db(out IServiceScope scope) { scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<AppDbContext>(); }

    static object Part(string pn, Guid site, Guid? typeId) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId,
    };

    async Task<Guid> FamilyAsync(ApiClient admin, string code, string label) =>
        (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code, label, description = (string?)null })).JsonAsync()).Id();

    async Task<Guid> PartAsync(ApiClient admin, string pn, Guid site, Guid? typeId) =>
        (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, typeId))).JsonAsync()).Id();

    async Task<Guid> ReleasedAsync(Guid site, Guid? partId)
    {
        var db = Db(out var s); using var _ = s;
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Test instruction", Number = "DWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, PartId = partId };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    static string Url(Guid? family) => $"{S}/parts/families/{family?.ToString() ?? "none"}";

    [Fact]
    public async Task Free_parts_in_one_family_are_deleted_and_other_families_stay()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "A" + tag, "Wipe family " + tag);
        var other = await FamilyAsync(admin, "B" + tag, "Keep family " + tag);
        var gone1 = await PartAsync(admin, "WA-" + tag, site, fam);
        var gone2 = await PartAsync(admin, "WB-" + tag, site, fam);
        var kept = await PartAsync(admin, "KA-" + tag, site, other);
        var loose = await PartAsync(admin, "NA-" + tag, site, null);

        var plan = await admin.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal("Wipe family " + tag, plan.Str("family"));
        Assert.Equal(2, plan.Int("total"));
        Assert.Equal(2, plan.Int("deletable"));
        Assert.Equal(0, plan.Int("skipped"));

        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(2, done.Int("deleted"));
        Assert.Equal(0, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());

        var db = Db(out var scope); using var _ = scope;
        Assert.False(await db.Parts.AnyAsync(p => p.Id == gone1 || p.Id == gone2));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == kept));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == loose));
        var audits = await db.AuditEntries.Where(a => (a.EntityId == gone1 || a.EntityId == gone2) && a.Action == "Deleted").ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.UserName));
            Assert.Equal("Part", a.EntityType);
            Assert.Contains("Deleted Part", a.Description);
            Assert.True(a.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task Parts_used_by_work_orders_instructions_or_parts_lists_are_skipped()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "U" + tag, "Used family " + tag);
        var other = await FamilyAsync(admin, "V" + tag, "Other family " + tag);
        var free = await PartAsync(admin, "UF-" + tag, site, fam);
        var onOrder = await PartAsync(admin, "UO-" + tag, site, fam);
        var onInstruction = await PartAsync(admin, "UI-" + tag, site, fam);
        var parent = await PartAsync(admin, "UP-" + tag, site, fam);
        var item = await PartAsync(admin, "UL-" + tag, site, fam);
        var neighbor = await PartAsync(admin, "UN-" + tag, site, other);

        var order = (await (await admin.PostOkAsync($"{S}/tracking", new { type = "Production", siteId = site, partId = onOrder })).JsonAsync()).Id();
        await ReleasedAsync(site, onInstruction);
        var list = (await (await admin.PostOkAsync($"{S}/parts-lists", new { title = "Kit " + tag, parentPartId = parent, listType = "Standard" })).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts-lists/{list}/items", new { partId = item, quantity = 1, required = true, traceability = "None" });

        var plan = await admin.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal(5, plan.Int("total"));
        Assert.Equal(1, plan.Int("deletable"));
        Assert.Equal(4, plan.Int("skipped"));
        JsonElement Skip(string pn) => plan.GetProperty("skips").EnumerateArray().Single(s => s.Str("partNumber") == pn);
        Assert.Equal("1 work order", Skip("UO-" + tag).Str("reason"));
        Assert.Equal(1, Skip("UO-" + tag).Int("workOrders"));
        Assert.Equal("1 work instruction", Skip("UI-" + tag).Str("reason"));
        Assert.Equal(1, Skip("UI-" + tag).Int("instructions"));
        Assert.Equal("1 parts list entry", Skip("UP-" + tag).Str("reason"));
        Assert.Equal("1 parts list entry", Skip("UL-" + tag).Str("reason"));
        Assert.Equal(1, Skip("UL-" + tag).Int("partsLists"));

        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(1, done.Int("deleted"));
        Assert.Equal(4, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal("1 work order", done.GetProperty("skips").EnumerateArray().Single(s => s.Str("partNumber") == "UO-" + tag).Str("reason"));

        var db = Db(out var scope); using var _ = scope;
        Assert.False(await db.Parts.AnyAsync(p => p.Id == free));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == onOrder));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == onInstruction));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == parent));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == item));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == neighbor));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/tracking/{order}")).StatusCode);
        Assert.False(await db.AuditEntries.AnyAsync(a => a.EntityId == onOrder && a.Action == "Deleted"));
    }

    [Fact]
    public async Task Empty_family_deletes_nothing()
    {
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "E" + tag, "Empty family " + tag);
        var plan = await admin.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal(0, plan.Int("total"));
        Assert.Equal(0, plan.Int("deletable"));
        Assert.Equal(0, plan.Int("skipped"));
        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(0, done.Int("deleted"));
        Assert.Equal(0, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{S}/parts/families/{Guid.NewGuid()}/delete")).StatusCode);
        var db = Db(out var scope); using var _ = scope;
        Assert.True(await db.SequflohReferenceValues.AnyAsync(v => v.Id == fam));
    }

    [Fact]
    public async Task Delete_family_needs_the_delete_records_permission()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "P" + tag, "Locked family " + tag);
        var part = await PartAsync(admin, "PL-" + tag, site, fam);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit], site);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"{Url(fam)}/delete-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{Url(fam)}/delete")).StatusCode);
        var db = Db(out var scope); using var _ = scope;
        Assert.True(await db.Parts.AnyAsync(p => p.Id == part));
    }

    [Fact]
    public async Task Deleting_a_family_removes_drawing_links_and_keeps_the_drawings()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "G" + tag, "Drawn family " + tag);
        var part = await PartAsync(admin, "GD-" + tag, site, fam);
        var folder = (await (await admin.PostOkAsync("/api/drawings/folders", new { name = "Spacers " + tag, siteId = site })).JsonAsync()).Id();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% family delete drawing\n%%EOF\n")), "file", "spacer.pdf" },
            { new StringContent("Spacer " + tag), "title" }, { new StringContent("A"), "revision" }, { new StringContent(part.ToString()), "partIds" } };
        var drawing = (await (await admin.Http.PostAsync($"/api/drawings/folders/{folder}/drawings", form)).JsonAsync()).Id();

        var done = await (await admin.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(1, done.Int("deleted"));
        Assert.Equal(0, done.Int("skipped"));
        var d = await admin.GetElAsync($"/api/drawings/{drawing}");
        Assert.Equal("Spacer " + tag, d.Str("title"));

        var db = Db(out var scope); using var _ = scope;
        Assert.False(await db.Parts.AnyAsync(p => p.Id == part));
        Assert.False(await db.DrawingPartLinks.AnyAsync(l => l.PartId == part || l.DrawingId == drawing));
        Assert.True(await db.Drawings.AnyAsync(x => x.Id == drawing));
        var keys = await db.DrawingRevisions.Where(r => r.DrawingId == drawing).Select(r => r.StorageKey).ToArrayAsync();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));
        var audit = await db.AuditEntries.Where(a => a.EntityId == part && a.Action == "Deleted").SingleAsync();
        Assert.Contains("drawings stay", audit.Description);
        Assert.False(string.IsNullOrWhiteSpace(audit.UserName));
    }
}
