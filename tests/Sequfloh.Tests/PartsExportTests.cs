using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>The Parts and Items CSV includes the fields on the part detail card, including the drawing revision and linked drawings.</summary>
[Collection("api")]
public sealed class PartsExportTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];
    static readonly string[] WithDrawings = [.. Editor, Permissions.SequflohViewDrawings];

    [Fact]
    public async Task Export_includes_the_detail_card_drawing_revision_and_linked_drawings()
    {
        var site = await app.CreateSiteAsync();
        var (_, viewer) = await app.CreateUserAsync(WithDrawings, site);
        var (_, hidden) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = (await viewer.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(x => x.Str("code") == "STRUTS").Id();
        var body = new
        {
            part = new { partNumber = "PX-" + tag, name = "Example bracket " + tag, description = "Bracket notes in the description", partType = 0, revision = "A", unitOfMeasure = "Each",
                productId = (Guid?)null, material = "Sample alloy", hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = true, isRepairPart = false, isSerialized = true, isLotControlled = false,
                drawingNumber = "BWS" + tag + "A", drawingRevision = "C", specification = "SPEC-" + tag, partClass = (string?)null, isAssembly = false, lotControl = "No", rowVersion = 0u },
            typeId = fam, notes = "=do not run", attributes = new Dictionary<string, string?>(),
        };
        var partId = (await (await viewer.PostOkAsync("/api/sequfloh/parts", body)).JsonAsync()).Id();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var folder = new DrawingFolder { Name = "Export " + tag, SiteId = site };
            db.DrawingFolders.Add(folder);
            await db.SaveChangesAsync();
            var same = new Drawing { FolderId = folder.Id, Title = "Sensor plate", DrawingNumber = "BWS" + tag + "A", CurrentRevision = "C", SiteId = site };
            var newer = new Drawing { FolderId = folder.Id, Title = "Mount", DrawingNumber = "MNT-" + tag, CurrentRevision = "E", SiteId = site };
            db.Drawings.AddRange(same, newer);
            await db.SaveChangesAsync();
            db.DrawingRevisions.AddRange(
                new DrawingRevision { DrawingId = same.Id, Revision = "C", FileName = "sensor-plate.pdf", ContentType = "application/pdf", StorageKey = "drawings/export-c", SizeBytes = 10, Sha256 = new string('a', 64), Status = DrawingRevisionStatus.Current, CreatedByName = "Export" },
                new DrawingRevision { DrawingId = newer.Id, Revision = "E", FileName = "mount.pdf", ContentType = "application/pdf", StorageKey = "drawings/export-e", SizeBytes = 10, Sha256 = new string('b', 64), Status = DrawingRevisionStatus.Current, CreatedByName = "Export" });
            db.DrawingPartLinks.AddRange(
                new DrawingPartLink { DrawingId = newer.Id, PartId = partId, CreatedAt = DateTimeOffset.UtcNow },
                new DrawingPartLink { DrawingId = same.Id, PartId = partId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var csv = (await (await viewer.Http.GetAsync($"/api/sequfloh/parts/export?q=PX-{tag}")).Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var head = lines[0].Split(',');
        var dwg = Array.IndexOf(head, "DWG #");
        Assert.True(dwg > 0);
        Assert.Equal(["Drawing revision", "Linked drawings", "Serialized", "Material", "Notes", "Created", "Last updated", "UPC / Barcode", "Specification", "Status"], head[(dwg + 1)..]);
        Assert.StartsWith("Part number,Name,Description,Product family,", lines[0]);

        var cells = Split(lines[1]);
        Assert.Equal("C", cells[dwg + 1]);
        Assert.Equal($"BWS{tag}A · Sensor plate · sensor-plate.pdf; MNT-{tag} · Mount · mount.pdf rev E", cells[dwg + 2]);
        Assert.Equal("Yes", cells[dwg + 3]);
        Assert.Equal("Sample alloy", cells[dwg + 4]);
        Assert.Equal("'=do not run", cells[dwg + 5]);
        Assert.Contains("·", cells[dwg + 6]);
        Assert.Contains("UTC", cells[dwg + 6]);
        Assert.Equal("SPEC-" + tag, cells[dwg + 9]);
        Assert.Equal("Active", cells[dwg + 10]);
        Assert.Contains("BWS" + tag + "A", cells[dwg]);

        var detail = (await viewer.GetElAsync($"/api/sequfloh/parts/{partId}")).GetProperty("part");
        Assert.Contains(detail.Str("createdBy"), cells[dwg + 6]);

        var without = (await (await hidden.Http.GetAsync($"/api/sequfloh/parts/export?q=PX-{tag}")).Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var hiddenCells = Split(without.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1]);
        Assert.Equal("C", hiddenCells[dwg + 1]);
        Assert.Equal("", hiddenCells[dwg + 2]);
    }

    /// <summary>Splits a CSV row that uses the export's quoting. Cells in this test do not contain commas inside quotes except where the exporter adds none.</summary>
    static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells;
    }
}
