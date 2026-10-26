using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Part Master bulk import from Excel, CSV and Word tables. Synthetic files only.</summary>
[Collection("api")]
public sealed class PartsImportTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static async Task<JsonElement> Read(ApiClient c, string name, byte[] bytes, bool allTables = false)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        var r = await c.Http.PostAsync($"/api/sequfloh/parts/import/read?allTables={allTables}", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    static object Request(JsonElement t, Guid site, bool update, bool keepRepeats = false) => new
    {
        fileName = t.GetProperty("fileName").GetString(),
        headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = update, keepRepeats, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
    };

    [Fact]
    public async Task Repeated_OR_numbers_with_other_values_can_be_kept_as_separate_parts_and_exact_copies_are_skipped()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var csv = $"OR #,Key Application,Position,NAPA US #\nRP-{k}-1,Example sedan,Front Left,N1\nRP-{k}-1,Example sedan,Front Left,N1\nRP-{k}-1,Example sedan,Front Right,N1\nRP-{k}-2,Example coupe,Rear,N2\n,Example van,Rear,N3\n";
        var t = await Read(eng, "repeats.csv", Encoding.UTF8.GetBytes(csv));

        var off = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, false))).JsonAsync();
        Assert.Equal(3, off.GetProperty("add").GetInt32());
        Assert.Equal(2, off.GetProperty("skip").GetInt32());
        var on = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, false, true))).JsonAsync();
        var rows = on.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["Add", "Skip", "Add", "Add", "Add"], rows.Select(r => r.GetProperty("action").GetString()).ToList());
        Assert.Contains("Exact duplicate of row 1", rows[1].GetProperty("messages")[0].GetString());
        Assert.True(rows[2].GetProperty("repeat").GetBoolean());
        Assert.False(rows[0].GetProperty("repeat").GetBoolean());
        Assert.Equal("N3", rows[4].GetProperty("partNumber").GetString()); // no part number: the row takes its other number
        var done = await (await eng.PostOkAsync("/api/sequfloh/parts/import", Request(t, site, false, true))).JsonAsync();
        Assert.Equal(4, done.GetProperty("add").GetInt32());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var same = await db.Parts.AsNoTracking().Where(p => p.PartNumber == $"RP-{k}-1").ToListAsync();
            Assert.Equal(2, same.Count); // the OR # is kept exactly, no suffix
            Assert.Equal(["Front Left", "Front Right"], same.Select(p => Sequfloh.Infrastructure.Modules.Execution.PartAttributes.Read(p.Attributes)["position"]).Order().ToList());
        }

        // the same file again: each part is matched by OR # and Position
        var again = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, true, true))).JsonAsync();
        Assert.Equal(0, again.GetProperty("add").GetInt32());
        Assert.Equal(4, again.GetProperty("update").GetInt32());
        var skip = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, false, true))).JsonAsync();
        Assert.Equal(0, skip.GetProperty("add").GetInt32());
    }

    [Fact]
    public async Task Reimport_matches_by_any_unique_number_and_placeholders_are_never_part_numbers()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        // made-up sheet: each row has one customer number or the other, every row has a DMA PN, and one JH PN is shared
        var csv = $"OR PN,NAPA PN,DMA PN,JH PN\nOR{k}1,/,DM{k}1,JH{k}\nOR{k}2,/,DM{k}2,JH{k}\n/,NP{k}3,DM{k}3,JH{k}\n/,NP{k}4,DM{k}4,N/A\n/,NP{k}4,DM{k}4,N/A\n";
        var t = await Read(eng, "pumps.csv", Encoding.UTF8.GetBytes(csv));
        Assert.Equal(0, t.GetProperty("mapping").GetProperty("partNumber").GetInt32());
        var first = await (await eng.PostOkAsync("/api/sequfloh/parts/import", Request(t, site, false))).JsonAsync();
        Assert.Equal(4, first.GetProperty("add").GetInt32()); // the "/" rows are added under their NAPA PN; the exact copy is skipped
        Assert.Equal(1, first.GetProperty("skip").GetInt32());

        // the same parts again, keyed on the DMA PN this time, with the family changed: every row updates and nothing is added
        var again = $"DMA PN,OR PN,NAPA PN,JH PN\nDM{k}1,OR{k}1,/,JH{k}\nDM{k}2,/,/,JH{k}\nDM{k}3,/,NP{k}3,JH{k}\nDM{k}4,-,NP{k}4,\n";
        var t2 = await Read(eng, "pumps2.csv", Encoding.UTF8.GetBytes(again));
        var map = t2.GetProperty("mapping").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
        map.Remove("attr:dmaPn"); map["partNumber"] = 0;
        var req = new { fileName = "pumps2.csv", headers = t2.GetProperty("headers"), rows = t2.GetProperty("rows"), mapping = map, siteId = site, updateExisting = true, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Injector" } };
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", req)).JsonAsync();
        Assert.Equal(["Update", "Update", "Update", "Update"], plan.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToList());
        await eng.PostOkAsync("/api/sequfloh/parts/import", req);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.SiteId == site).ToListAsync();
        Assert.Equal(4, parts.Count);
        Assert.DoesNotContain(parts, p => p.PartNumber == "/");
        Assert.All(parts, p => Assert.Equal("Fuel Injector", p.Name));

        // a row whose numbers belong to two different parts is an error, not a guess
        var mixed = $"OR PN,NAPA PN\nOR{k}1,NP{k}3\n";
        var t3 = await Read(eng, "mixed.csv", Encoding.UTF8.GetBytes(mixed));
        var bad = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t3, site, true))).JsonAsync();
        Assert.Equal("Error", bad.GetProperty("rows")[0].GetProperty("action").GetString());
        Assert.Contains("more than one part", bad.GetProperty("rows")[0].GetProperty("messages")[0].GetString());
    }

    public static byte[] Docx(params string[][][] tables)
    {
        var body = new StringBuilder();
        foreach (var t in tables)
        {
            body.Append("<w:p><w:r><w:t>Parts</w:t></w:r></w:p><w:tbl>");
            foreach (var row in t) body.Append("<w:tr>").Append(string.Concat(row.Select(c => $"<w:tc><w:p><w:r><w:t>{System.Security.SecurityElement.Escape(c)}</w:t></w:r></w:p></w:tc>"))).Append("</w:tr>");
            body.Append("</w:tbl>");
        }
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string n, string x) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(x); }
            Add("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("word/document.xml", $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>");
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Csv_maps_headers_previews_duplicates_and_imports_in_one_audited_step()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        // an existing part, to be skipped or updated
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parts.Add(new Sequfloh.Domain.MasterData.Part { PartNumber = $"IMP-{k}-1", Name = "Old name", Revision = "A", UnitOfMeasure = "EA", SiteId = site });
            await db.SaveChangesAsync();
        }
        var csv = $"Item Number,Description,Rev,Category,Repairable,Serial Number\nIMP-{k}-1,Example bracket,A,Struts,yes,S1\nIMP-{k}-2,Example screw M4,B,gas lift,no,S2\nIMP-{k}-2,Example screw again,B,gas lift,no,S3\n,No number,A,gas lift,no,\nIMP-{k}-3,Example nut,A,gas lift,maybe,\n";
        var t = await Read(eng, "parts.csv", Encoding.UTF8.GetBytes(csv));
        var map = t.GetProperty("mapping");
        Assert.Equal(0, map.GetProperty("partNumber").GetInt32());
        Assert.Equal(1, map.GetProperty("description").GetInt32());
        Assert.False(map.TryGetProperty("revision", out _)); // parts have no revision, so a Rev column is not offered
        Assert.Equal(3, map.GetProperty("typeName").GetInt32());
        Assert.Equal(4, map.GetProperty("isRepairable").GetInt32());
        Assert.Equal(4, map.EnumerateObject().Count());
        Assert.Contains(t.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Serial"));

        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, false))).JsonAsync();
        Assert.Equal(1, plan.GetProperty("add").GetInt32());
        Assert.Equal(0, plan.GetProperty("update").GetInt32());
        Assert.Equal(3, plan.GetProperty("skip").GetInt32()); // existing, repeat in file, no number
        Assert.Equal(1, plan.GetProperty("errors").GetInt32()); // bad Yes/No
        var actions = plan.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToList();
        Assert.Equal(["Skip", "Add", "Skip", "Skip", "Error"], actions);

        var upd = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, true))).JsonAsync();
        Assert.Equal(1, upd.GetProperty("update").GetInt32());
        var done = await (await eng.PostOkAsync("/api/sequfloh/parts/import", Request(t, site, true))).JsonAsync();
        Assert.Equal(1, done.GetProperty("add").GetInt32());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith($"IMP-{k}-")).OrderBy(p => p.PartNumber).ToListAsync();
            Assert.Equal(2, parts.Count);
            Assert.Equal("Fuel Pump", parts[0].Name); // the Name chosen for all rows
            Assert.Equal("Example bracket", parts[0].Description); Assert.True(parts[0].IsRepairable); Assert.Equal("Struts", parts[0].ProductFamily); Assert.NotNull(parts[0].TypeId);
            Assert.Equal("Fuel Pump", parts[1].Name); Assert.False(parts[1].IsRepairable);
            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityType == "PartsImport" && a.SiteId == site).SingleAsync();
            Assert.Contains("parts.csv: 1 added, 1 updated, 2 skipped, 1 with errors", audit.Description);
        }
    }

    [Fact]
    public async Task Excel_and_Word_tables_are_read_and_viewers_cannot_import()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        byte[] xlsx;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Parts");
            ws.Cell(1, 1).Value = "Example parts export";
            ws.Cell(3, 1).Value = "Part Number"; ws.Cell(3, 2).Value = "Name"; ws.Cell(3, 3).Value = "UOM"; ws.Cell(3, 4).Value = "Repair part";
            ws.Cell(4, 1).Value = $"XL-{k}-1"; ws.Cell(4, 2).Value = "Example top mount"; ws.Cell(4, 3).Value = "EA"; ws.Cell(4, 4).Value = "Y";
            var ms = new MemoryStream(); wb.SaveAs(ms); xlsx = ms.ToArray();
        }
        var t = await Read(eng, "export.xlsx", xlsx);
        Assert.Equal(["Part Number", "Name", "UOM", "Repair part"], t.GetProperty("headers").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(3, t.GetProperty("mapping").GetProperty("isRepairPart").GetInt32());
        await eng.PostOkAsync("/api/sequfloh/parts/import", Request(t, site, false));

        var hdr = new[] { "Part No", "Part Name" };
        var doc = Docx([hdr, [$"WD-{k}-1", "Example washer"]], [hdr, [$"WD-{k}-2", "Example spring"]], [["Other", "Table"], ["a", "b"]]);
        var first = await Read(eng, "parts.docx", doc);
        Assert.Single(first.GetProperty("rows").EnumerateArray());
        var all = await Read(eng, "parts.docx", doc, allTables: true);
        Assert.Equal(2, all.GetProperty("rows").GetArrayLength());
        await eng.PostOkAsync("/api/sequfloh/parts/import", Request(all, site, false));

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Http.PostAsJsonAsync("/api/sequfloh/parts/import", Request(all, site, false))).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await db.Parts.SingleAsync(p => p.PartNumber == $"XL-{k}-1")).IsRepairPart);
        Assert.Equal(2, await db.Parts.CountAsync(p => p.PartNumber.StartsWith($"WD-{k}-")));
    }
}
