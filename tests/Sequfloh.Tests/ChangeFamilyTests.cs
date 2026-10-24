using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Domain.Drawings;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Tests;

/// <summary>Moving many parts to another product family at once. Synthetic data only.</summary>
[Collection("api")]
public sealed class ChangeFamilyTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];
    const string U = "/api/sequfloh/parts/change-family";

    sealed record Setup(Guid Site, ApiClient C, Guid Pump, Guid Injector, Guid Root, Guid Sub, Guid InjA, Guid InjB, Guid PumpA, Guid Loose, string Tag);

    async Task<Setup> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fam = await db.SequflohReferenceValues.Where(v => v.ListKey == "product-families").ToDictionaryAsync(v => v.Code, v => v);
        var pump = fam["FUEL_PUMP"]; var inj = fam["FUEL_INJECTOR"];
        Part P(string pn, string? jh = null)
        {
            var p = new Part { PartNumber = pn, Name = "Example " + pn, SiteId = site, Status = MasterStatus.Active, TypeId = pump.Id, ProductFamily = pump.Label };
            PartAttributes.Write(p, new Dictionary<string, string?> { ["jhPn"] = jh, ["napaPn"] = "NP-" + pn, ["boxSize"] = "B2" });
            db.Parts.Add(p); return p;
        }
        var injA = P("INJ-A" + tag, "INJ-A" + tag); var injB = P("EXB" + tag, "INJ-B" + tag); var pumpA = P("EXP" + tag, "PMP" + tag); var loose = P("EXL" + tag);
        var root = new DrawingFolder { Name = "Fuel Injector Drawings " + tag, SiteId = site };
        db.DrawingFolders.Add(root); await db.SaveChangesAsync();
        var sub = new DrawingFolder { Name = "INJ-B" + tag, ParentId = root.Id, SiteId = site };
        var pumps = new DrawingFolder { Name = "Fuel Pump Drawings " + tag, SiteId = site };
        db.DrawingFolders.AddRange(sub, pumps); await db.SaveChangesAsync();
        Drawing D(Guid f, string t, Part p) { var d = new Drawing { FolderId = f, Title = t, SiteId = site }; db.Drawings.Add(d); db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = d.Id, PartId = p.Id, CreatedAt = DateTimeOffset.UtcNow }); return d; }
        D(root.Id, "INJ-A" + tag, injA); D(sub.Id, "INJ-B" + tag, injB); D(pumps.Id, "PMP" + tag, pumpA);
        await db.SaveChangesAsync();
        return new(site, c, pump.Id, inj.Id, root.Id, sub.Id, injA.Id, injB.Id, pumpA.Id, loose.Id, tag);
    }

    static List<Guid> Ids(JsonElement e, string prop = "parts") => e.GetProperty(prop).EnumerateArray().Select(x => x.Id()).ToList();

    [Fact]
    public async Task Folder_mode_selects_the_family_parts_linked_to_drawings_in_the_folder_and_subfolders_and_lists_the_unmatched()
    {
        var s = await SetupAsync();
        var pv = await (await s.C.PostOkAsync(U + "/preview", new { mode = "folder", folderId = s.Root, sourceTypeId = s.Pump, targetTypeId = s.Injector })).JsonAsync();
        Assert.Equal(new[] { s.InjB, s.InjA }.Order(), Ids(pv).Order());
        Assert.Equal(2, pv.Int("count"));
        var row = pv.GetProperty("parts").EnumerateArray().First(x => x.Id() == s.InjB);
        Assert.Equal("INJ-B" + s.Tag, row.Str("drawing"));
        Assert.Equal("Fuel Pump", row.Str("family"));
        Assert.Equal("INJ-B" + s.Tag, row.GetProperty("numbers").Str("jhPn"));
        // the source family's parts with no drawing at all, to check by hand
        var unmatched = Ids(pv, "unmatched");
        Assert.Contains(s.Loose, unmatched);
        Assert.DoesNotContain(s.PumpA, unmatched);
        var x = await s.C.Http.GetAsync($"{U}/unmatched.xlsx?sourceTypeId={s.Pump}");
        Assert.Equal(HttpStatusCode.OK, x.StatusCode);
        using var wb = new ClosedXML.Excel.XLWorkbook(await x.Content.ReadAsStreamAsync());
        Assert.Contains(wb.Worksheet(1).RowsUsed(), r => r.Cell(1).GetString() == "EXL" + s.Tag);
    }

    [Fact]
    public async Task Pattern_mode_matches_any_number_ignoring_case()
    {
        var s = await SetupAsync();
        var pv = await (await s.C.PostOkAsync(U + "/preview", new { mode = "pattern", sourceTypeId = s.Pump, pattern = "inj-", targetTypeId = s.Injector })).JsonAsync();
        var ids = Ids(pv);
        Assert.Contains(s.InjA, ids); Assert.Contains(s.InjB, ids); // INJ-B only in its JH PN
        Assert.DoesNotContain(s.PumpA, ids);
        pv = await (await s.C.PostOkAsync(U + "/preview", new { mode = "pattern", sourceTypeId = s.Pump, pattern = "b" + s.Tag.ToLowerInvariant(), patternMode = "contains" })).JsonAsync();
        Assert.Equal(new[] { s.InjB }, Ids(pv));
    }

    [Fact]
    public async Task Apply_moves_the_parts_audits_each_and_the_run_and_moving_back_keeps_every_value()
    {
        var s = await SetupAsync();
        var r = await (await s.C.PostOkAsync(U, new { mode = "folder", folderId = s.Root, sourceTypeId = s.Pump, targetTypeId = s.Injector })).JsonAsync();
        Assert.Equal(2, r.Int("changed"));
        var p = (await s.C.GetElAsync($"/api/sequfloh/parts/{s.InjB}")).GetProperty("part");
        Assert.Equal("Fuel Injector", p.Str("typeName"));
        Assert.Equal("NP-EXB" + s.Tag, p.GetProperty("attributes").Str("napaPn")); // kept though Fuel Injector does not show it
        Assert.Equal("Fuel Pump", (await s.C.GetElAsync($"/api/sequfloh/parts/{s.PumpA}")).GetProperty("part").Str("typeName"));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audits = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == s.InjA || a.EntityId == s.InjB).ToListAsync();
            Assert.Equal(2, audits.Count(a => a.Action == "Updated" && a.Changes!.Contains("Fuel Injector")));
            Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.Action == "Changed product family").ToListAsync(),
                a => a.Description.Contains("Moved 2 parts to product family Fuel Injector (2 from Fuel Pump)") && a.Description.Contains("Fuel Injector Drawings " + s.Tag) && a.UserName != null);
        }

        // moving back by ticked parts restores the family; nothing was lost
        r = await (await s.C.PostOkAsync(U, new { mode = "ids", partIds = new[] { s.InjA, s.InjB }, targetTypeId = s.Pump })).JsonAsync();
        Assert.Equal(2, r.Int("changed"));
        var attrs = (await s.C.GetElAsync($"/api/sequfloh/parts/{s.InjB}")).GetProperty("part").GetProperty("attributes");
        Assert.Equal("NP-EXB" + s.Tag, attrs.Str("napaPn")); Assert.Equal("B2", attrs.Str("boxSize")); Assert.Equal("INJ-B" + s.Tag, attrs.Str("jhPn"));
        // nothing left to change
        Assert.Equal(HttpStatusCode.BadRequest, (await s.C.Http.PostAsJsonAsync(U, new { mode = "ids", partIds = new[] { s.InjA }, targetTypeId = s.Pump })).StatusCode);
    }

    [Fact]
    public async Task Editing_a_part_keeps_the_values_of_fields_its_family_does_not_show()
    {
        var s = await SetupAsync();
        await s.C.PostOkAsync(U, new { mode = "ids", partIds = new[] { s.InjB }, targetTypeId = s.Injector });
        var p = (await s.C.GetElAsync($"/api/sequfloh/parts/{s.InjB}")).GetProperty("part");
        var body = new
        {
            part = new { partNumber = p.Str("partNumber"), name = "Renamed", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = s.Site, departmentId = (Guid?)null, status = 0, rowVersion = p.GetProperty("rowVersion").GetUInt32() },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
            typeId = s.Injector, attributes = new Dictionary<string, string?> { ["jhPn"] = "INJ-B" + s.Tag },
        };
        var r = await s.C.Http.PutAsJsonAsync($"/api/sequfloh/parts/{s.InjB}", body);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var attrs = (await s.C.GetElAsync($"/api/sequfloh/parts/{s.InjB}")).GetProperty("part").GetProperty("attributes");
        Assert.Equal("NP-EXB" + s.Tag, attrs.Str("napaPn"));
        Assert.Equal("INJ-B" + s.Tag, attrs.Str("jhPn"));
    }

    [Fact]
    public async Task Without_part_edit_permission_the_change_is_refused()
    {
        var s = await SetupAsync();
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.MasterDataView], s.Site);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync(U, new { mode = "ids", partIds = new[] { s.InjA }, targetTypeId = s.Injector })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync(U + "/preview", new { mode = "folder", folderId = s.Root, sourceTypeId = s.Pump })).StatusCode);
        var (_, half) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataView], s.Site);
        Assert.Equal(HttpStatusCode.Forbidden, (await half.Http.PostAsJsonAsync(U, new { mode = "ids", partIds = new[] { s.InjA }, targetTypeId = s.Injector })).StatusCode);
        Assert.Equal("Fuel Pump", (await s.C.GetElAsync($"/api/sequfloh/parts/{s.InjA}")).GetProperty("part").Str("typeName"));
    }
}
