using System.Diagnostics;
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

    /// <summary>
    /// A family large enough to cross two delete batches, with parts that must stay: no delete permission, an open work order, a work instruction, and parts-list uses.
    /// The timing lines compare this batch delete with single-part delete on the same test database. They are not production measurements.
    /// </summary>
    [Fact]
    public async Task A_large_mixed_family_is_deleted_in_batches_and_keeps_referenced_parts()
    {
        const int bulk = 220;
        const int timed = 24;
        var site = await app.CreateSiteAsync();
        var otherSite = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "M" + tag, "Mixed family " + tag);
        var other = await FamilyAsync(admin, "N" + tag, "Untouched family " + tag);
        var clock = await FamilyAsync(admin, "C" + tag, "Clock family " + tag);

        var timedIds = new Guid[timed];
        {
            var db = Db(out var scope); using var hold = scope;
            for (var i = 0; i < timed; i++)
                timedIds[i] = db.Parts.Add(NewPart($"T{i:000}-" + tag, site, clock)).Entity.Id;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/sequfloh/parts/{timedIds[0]}")).StatusCode);
        var single = Stopwatch.StartNew();
        for (var i = 1; i < timed; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/sequfloh/parts/{timedIds[i]}")).StatusCode);
        single.Stop();

        var ids = new Guid[bulk];
        Guid linked, revised, component, recentId;
        {
            var db = Db(out var scope); using var hold = scope;
            var adminId = await db.Users.Where(u => u.Email == TestApp.AdminEmail).Select(u => u.Id).FirstAsync();
            for (var i = 0; i < bulk; i++)
                ids[i] = db.Parts.Add(NewPart($"B{i:000}-" + tag, site, fam)).Entity.Id;
            linked = ids[0]; revised = ids[1]; component = ids[2]; recentId = ids[3];
            db.PartRevisions.Add(new PartRevision { PartId = revised, Revision = "A", Reason = "Initial release", EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow) });
            var neighbor = NewPart("KA-" + tag, site, other);
            db.Parts.Add(neighbor);
            db.PartRevisions.Add(new PartRevision { PartId = neighbor.Id, Revision = "A", Reason = "Keep", EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow) });
            db.PartComponentLinks.Add(new PartComponentLink { PartId = neighbor.Id, ComponentPartId = component, Kind = PartLinkKind.PartsList, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "Test" });
            db.SequflohUserItems.Add(new SequflohUserItem { UserId = adminId, Kind = "recent", ItemType = "part", RecordId = recentId, Path = "/sequfloh/parts/" + recentId, Label = "Bulk recent", LastOpenedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var neighborId = await NeighborId(admin, "KA-" + tag);

        var onOrder = await PartAsync(admin, "UO-" + tag, site, fam);
        var onInstruction = await PartAsync(admin, "UI-" + tag, site, fam);
        var parent = await PartAsync(admin, "UP-" + tag, site, fam);
        var item = await PartAsync(admin, "UL-" + tag, site, fam);
        var held = await PartAsync(admin, "UH-" + tag, site, fam);
        { var db = Db(out var s); using var move = s; var row = await db.Parts.SingleAsync(p => p.Id == held); row.SiteId = otherSite; await db.SaveChangesAsync(); }
        var order = (await (await admin.PostOkAsync($"{S}/tracking", new { type = "Production", siteId = site, partId = onOrder })).JsonAsync()).Id();
        var instruction = await ReleasedAsync(site, onInstruction);
        var list = (await (await admin.PostOkAsync($"{S}/parts-lists", new { title = "Kit " + tag, parentPartId = parent, listType = "Standard" })).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts-lists/{list}/items", new { partId = item, quantity = 1, required = true, traceability = "None" });
        var folder = (await (await admin.PostOkAsync("/api/drawings/folders", new { name = "Spacers " + tag, siteId = site, partId = linked })).JsonAsync()).Id();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n% mixed family drawing\n%%EOF\n")), "file", "spacer.pdf" },
            { new StringContent("Spacer " + tag), "title" }, { new StringContent("A"), "revision" }, { new StringContent(linked.ToString()), "partIds" } };
        var drawing = (await (await admin.Http.PostAsync($"/api/drawings/folders/{folder}/drawings", form)).JsonAsync()).Id();
        string[] drawingKeys;
        { var db = Db(out var s); using var read = s; drawingKeys = await db.DrawingRevisions.Where(r => r.DrawingId == drawing).Select(r => r.StorageKey).ToArrayAsync(); }
        Assert.NotEmpty(drawingKeys);
        Assert.All(drawingKeys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));

        var (userId, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohDeleteRecords], site);
        var viewRole = await (await admin.PostOkAsync("/api/admin/roles", new { name = "R" + Guid.NewGuid().ToString("N"), description = (string?)null, permissions = new[] { Permissions.SequflohAccess } })).Content.ReadFromJsonAsync<Guid>();
        await admin.PostOkAsync($"/api/admin/users/{userId}/roles", new { roleId = viewRole, siteId = otherSite, departmentId = (Guid?)null });

        var plan = await user.GetElAsync($"{Url(fam)}/delete-preview");
        Assert.Equal("Mixed family " + tag, plan.Str("family"));
        Assert.Equal(bulk + 5, plan.Int("total"));
        Assert.Equal(bulk, plan.Int("deletable"));
        Assert.Equal(5, plan.Int("skipped"));
        JsonElement Skip(JsonElement doc, string pn) => doc.GetProperty("skips").EnumerateArray().Single(s => s.Str("partNumber") == pn);
        Assert.Equal("You do not have permission to delete this part.", Skip(plan, "UH-" + tag).Str("reason"));
        Assert.Equal("1 work order", Skip(plan, "UO-" + tag).Str("reason"));
        Assert.Equal(1, Skip(plan, "UO-" + tag).Int("workOrders"));
        Assert.Equal("1 work instruction", Skip(plan, "UI-" + tag).Str("reason"));
        Assert.Equal(1, Skip(plan, "UI-" + tag).Int("instructions"));
        Assert.Equal("1 parts list entry", Skip(plan, "UP-" + tag).Str("reason"));
        Assert.Equal(1, Skip(plan, "UP-" + tag).Int("partsLists"));
        Assert.Equal("1 parts list entry", Skip(plan, "UL-" + tag).Str("reason"));
        Assert.Equal(1, Skip(plan, "UL-" + tag).Int("partsLists"));

        var familyWatch = Stopwatch.StartNew();
        var calls = 0;
        var deleted = 0;
        JsonElement done = default;
        do
        {
            calls++;
            done = await (await user.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
            deleted += done.Int("deleted");
        } while (done.GetProperty("more").GetBoolean() && calls < 5);
        familyWatch.Stop();
        var singleEach = single.Elapsed.TotalMilliseconds / (timed - 1);
        var familyEach = familyWatch.Elapsed.TotalMilliseconds / bulk;
        var ratio = singleEach / familyEach;
        var timing = $"Test database only, not production. Single delete of {timed - 1} parts took {single.Elapsed.TotalMilliseconds:0} ms ({singleEach:0.##} ms each, one request per part). Family delete of {bulk} parts took {familyWatch.Elapsed.TotalMilliseconds:0} ms ({familyEach:0.##} ms each) in {calls} request(s). Per-part ratio {ratio:0.##}x.";
        Console.WriteLine(timing);
        Assert.True(ratio >= 3, timing);
        Assert.Equal(1, calls);
        Assert.Equal(bulk, deleted);
        Assert.Equal(5, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal("You do not have permission to delete this part.", Skip(done, "UH-" + tag).Str("reason"));
        Assert.Equal("1 work order", Skip(done, "UO-" + tag).Str("reason"));
        Assert.Equal("1 work instruction", Skip(done, "UI-" + tag).Str("reason"));
        Assert.Equal("1 parts list entry", Skip(done, "UP-" + tag).Str("reason"));
        Assert.Equal("1 parts list entry", Skip(done, "UL-" + tag).Str("reason"));

        var again = await (await user.PostOkAsync($"{Url(fam)}/delete")).JsonAsync();
        Assert.Equal(0, again.Int("deleted"));
        Assert.Equal(5, again.Int("skipped"));
        Assert.False(again.GetProperty("more").GetBoolean());
        Assert.Equal("1 work order", Skip(again, "UO-" + tag).Str("reason"));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{S}/parts/{linked}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/parts/{onOrder}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/parts/{neighborId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{S}/tracking/{order}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/drawings/{drawing}")).StatusCode);
        Assert.All(drawingKeys, k => Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, k))));

        var dbAfter = Db(out var scopeAfter); using var end = scopeAfter;
        Assert.False(await dbAfter.Parts.AnyAsync(p => ids.Contains(p.Id)));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == held && p.SiteId == otherSite));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == onOrder));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == onInstruction));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == parent));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == item));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == neighborId));
        Assert.Equal(TrackingStatus.NotStarted, await dbAfter.SequflohTrackingRecords.Where(t => t.Id == order).Select(t => t.Status).SingleAsync());
        Assert.Equal(onOrder, await dbAfter.SequflohTrackingRecords.Where(t => t.Id == order).Select(t => t.PartId).SingleAsync());
        Assert.True(await dbAfter.SequflohWorkInstructions.AnyAsync(w => w.Id == instruction && w.PartId == onInstruction));
        Assert.True(await dbAfter.SequflohPartsLists.AnyAsync(l => l.Id == list && l.ParentPartId == parent));
        Assert.True(await dbAfter.SequflohPartsListItems.AnyAsync(i => i.PartsListId == list && i.PartId == item));
        Assert.True(await dbAfter.SequflohReferenceValues.AnyAsync(v => v.Id == fam));
        Assert.True(await dbAfter.SequflohReferenceValues.AnyAsync(v => v.Id == other));
        Assert.False(await dbAfter.DrawingPartLinks.AnyAsync(l => l.PartId == linked || ids.Contains(l.PartId)));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == drawing));
        Assert.True(await dbAfter.DrawingFolders.AnyAsync(f => f.Id == folder && f.PartId == null));
        Assert.False(await dbAfter.PartRevisions.AnyAsync(r => r.PartId == revised));
        Assert.True(await dbAfter.PartRevisions.AnyAsync(r => r.PartId == neighborId));
        Assert.False(await dbAfter.PartComponentLinks.AnyAsync(l => l.ComponentPartId == component || l.PartId == component));
        Assert.False(await dbAfter.SequflohUserItems.AnyAsync(i => i.RecordId == recentId));
        var audits = await dbAfter.AuditEntries.Where(a => ids.Contains(a.EntityId) && a.Action == "Deleted").ToListAsync();
        Assert.Equal(bulk, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.UserName));
            Assert.Equal("Part", a.EntityType);
            Assert.Contains("Deleted Part", a.Description);
            Assert.Contains(" rev ", a.Description);
            Assert.True(a.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
        var linkedAudit = audits.Single(a => a.EntityId == linked);
        Assert.Contains("removed its links to 1 drawing (the drawings stay)", linkedAudit.Description);
        Assert.DoesNotContain("drawing", audits.Single(a => a.EntityId == ids[4]).Description, StringComparison.OrdinalIgnoreCase);
        Assert.False(await dbAfter.AuditEntries.AnyAsync(a => (a.EntityId == held || a.EntityId == onOrder || a.EntityId == onInstruction || a.EntityId == parent || a.EntityId == item || a.EntityId == neighborId) && a.Action == "Deleted"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/parts/{neighborId}")).StatusCode);
        Assert.False(await dbAfter.Parts.AnyAsync(p => p.Id == neighborId));
        var singleAudit = await dbAfter.AuditEntries.Where(a => a.EntityId == neighborId && a.Action == "Deleted").SingleAsync();
        Assert.Contains("Deleted Part", singleAudit.Description);
        Assert.False(string.IsNullOrWhiteSpace(singleAudit.UserName));
        Assert.True(await dbAfter.Parts.AnyAsync(p => p.Id == onOrder));
        Assert.True(await dbAfter.Drawings.AnyAsync(d => d.Id == drawing));
    }

    [Fact]
    public async Task A_reference_added_while_the_part_is_locked_keeps_the_part()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = await FamilyAsync(admin, "R" + tag, "Race family " + tag);
        var part = await PartAsync(admin, "RC-" + tag, site, fam);
        var db = Db(out var scope); using var hold = scope;
        await using var tx = await db.Database.BeginTransactionAsync();
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Race instruction " + tag, Number = "DWI-" + tag, Revision = "A", SiteId = site, Status = WorkInstructionStatus.Released, PartId = part };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        var pending = admin.PostAsync($"{Url(fam)}/delete");
        await Task.Delay(400);
        await tx.CommitAsync();
        var done = await (await pending).JsonAsync();
        Assert.Equal(0, done.Int("deleted"));
        Assert.Equal(1, done.Int("skipped"));
        Assert.False(done.GetProperty("more").GetBoolean());
        Assert.Equal("RC-" + tag, done.GetProperty("skips").EnumerateArray().Single().Str("partNumber"));
        Assert.Equal("1 work instruction", done.GetProperty("skips").EnumerateArray().Single().Str("reason"));
        Assert.True(await db.Parts.AnyAsync(p => p.Id == part));
        Assert.True(await db.SequflohWorkInstructions.AnyAsync(w => w.Id == wi.Id && w.PartId == part));
    }

    static Part NewPart(string pn, Guid site, Guid typeId) => new()
    {
        PartNumber = pn, Name = "Example part " + pn, Revision = "A", UnitOfMeasure = "EA", SiteId = site, TypeId = typeId, Status = MasterStatus.Active,
    };

    async Task<Guid> NeighborId(ApiClient admin, string pn)
    {
        var db = Db(out var scope); using var _ = scope;
        return await db.Parts.Where(p => p.PartNumber == pn).Select(p => p.Id).SingleAsync();
    }
}
