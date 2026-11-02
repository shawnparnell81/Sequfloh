using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Scanning a UPC on a filled validation form fills in the part details. Synthetic data only.</summary>
[Collection("api")]
public sealed class UpcFillTests(TestApp app)
{
    static string Upc()
    {
        var d = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();
        var sum = d.Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        return d + ((10 - sum % 10) % 10);
    }

    static byte[] Form()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Form");
        ws.Cell(1, 1).Value = "Example validation form";
        ws.Cell(2, 1).Value = "Part No.:";
        ws.Cell(3, 1).Value = "Description";
        ws.Cell(4, 1).Value = "Product family:";
        ws.Cell(5, 1).Value = "DWG #"; ws.Cell(5, 2).Value = "already typed";
        ws.Cell(6, 1).Value = "Result";
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    [Fact]
    public async Task A_scan_fills_the_labelled_cells_sets_part_and_family_and_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles], site);
        var upc = Upc(); var pn = "UF-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid family;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fam = await db.SequflohReferenceValues.FirstAsync(v => v.ListKey == "product-families" && v.Label == "Struts");
            family = fam.Id;
            db.Parts.Add(new Part { PartNumber = pn, Name = "Struts", Description = "Example front strut", DrawingNumber = "DW-1", Upc = upc, UpcKey = Sequfloh.Infrastructure.Services.Gtin.Key(upc),
                TypeId = fam.Id, ProductFamily = fam.Label, SiteId = site });
            await db.SaveChangesAsync();
        }
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Form()), "file", "Example form.xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        form.Add(new StringContent("true"), "blankForm");
        var blankId = (await (await editor.Http.PostAsync("/api/imported-files", form)).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.Http.PostAsJsonAsync($"/api/imported-files/{blankId}/upc-fill", new { code = upc })).StatusCode);
        var filledId = (await (await editor.PostAsync($"/api/imported-files/{blankId}/fill-out", new { title = "Example copy" })).JsonAsync()).Id();
        var url = $"/api/imported-files/{filledId}/upc-fill";

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync(url, new { code = upc })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.Http.PostAsJsonAsync(url, new { code = "ABC" })).StatusCode);

        var none = await (await editor.PostOkAsync(url, new { code = Upc() })).JsonAsync();
        Assert.False(none.GetProperty("found").GetBoolean());
        Assert.Empty(none.GetProperty("edits").EnumerateArray());

        var r = await (await editor.PostOkAsync(url, new { code = upc, sheet = "Form" })).JsonAsync();
        Assert.True(r.GetProperty("found").GetBoolean());
        var edits = r.GetProperty("edits").EnumerateArray().ToDictionary(e => e.Int("r"), e => (C: e.Int("c"), V: e.Str("value")));
        Assert.Equal((2, pn), edits[2]);
        Assert.Equal((2, "Example front strut"), edits[3]);
        Assert.Equal((2, "Struts"), edits[4]);
        Assert.False(edits.ContainsKey(5)); // a cell that already holds a value is never replaced
        Assert.False(edits.ContainsKey(6));

        var fd = await editor.GetElAsync($"/api/imported-files/{filledId}");
        Assert.Equal(pn, fd.Str("partNumber"));
        Assert.Equal(family, fd.GetProperty("familyId").GetGuid());
        using var s2 = app.Services.CreateScope();
        var audit = await s2.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking().Where(a => a.EntityId == filledId && a.Action == "Filled from UPC scan").SingleAsync();
        Assert.StartsWith($"Part details filled from UPC scan {upc} (part {pn})", audit.Description);
        Assert.NotNull(audit.UserName);
    }

    [Fact]
    public void Labels_match_without_case_or_colon_and_each_field_fills_once()
    {
        var p = new Part { PartNumber = "X-1", Description = "D" };
        GridCell Cell(int r, int c, string? t) => new(r, c, t, null, null, false, null, 1, 1, null, false, null);
        var cells = new List<GridCell> { Cell(1, 1, "PART NUMBER :"), Cell(2, 1, "P/N"), Cell(3, 1, "desc."), Cell(4, 1, "Notes") };
        var (edits, filled) = ImportedFileService.UpcEdits("S", cells, ImportedFileService.UpcFields(p, null));
        Assert.Equal(["Part number", "Description"], filled);
        Assert.Equal([(1, 2), (3, 2)], edits.Select(e => (e.R, e.C)).ToList());
    }

    [Theory]
    [InlineData("Part Number", "Drawing Number")]
    [InlineData("Part #", "Drawing #")]
    [InlineData("P/N:", "DWG #")]
    [InlineData("PN", "DWG No")]
    [InlineData("Part No.", "DWG No.:")]
    [InlineData("Part#", "DWG#")]
    public void The_usual_part_and_drawing_number_labels_are_matched(string pnLabel, string dwgLabel)
    {
        var p = new Part { PartNumber = "X-1", DrawingNumber = "DW-9" };
        GridCell Cell(int r, int c, string? t) => new(r, c, t, null, null, false, null, 1, 1, null, false, null);
        var (edits, filled) = ImportedFileService.UpcEdits("S", [Cell(1, 1, pnLabel), Cell(2, 1, dwgLabel)], ImportedFileService.UpcFields(p, null));
        Assert.Equal(["Part number", "Drawing number"], filled);
        Assert.Equal(["X-1", "DW-9"], edits.Select(e => e.Value).ToList());
    }
}
