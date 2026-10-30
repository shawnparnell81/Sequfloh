using System.Net.Http.Headers;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Part Master import of every tab of a workbook, the tab name being the product family. Made-up parts only.</summary>
[Collection("api")]
public sealed class PartsImportTabsTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];
    const string P = "/api/sequfloh/parts/import";

    static async Task<JsonElement> Read(ApiClient c, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "made-up-families.xlsx");
        var r = await c.Http.PostAsync($"{P}/read?allTabs=true", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    static object Request(JsonElement t, Guid site, bool update = true) => new
    {
        fileName = t.GetProperty("fileName").GetString(), headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"),
        siteId = site, updateExisting = update, tabs = t.GetProperty("tabs"), skippedTabs = t.GetProperty("skippedTabs"),
    };

    // five product family tabs (one family added at runtime) with their own headings, and one tab that is no product family
    static byte[] Workbook(string k, string runtimeFamily, string desc = "Made-up description", string material = "Made-up steel")
    {
        using var wb = new XLWorkbook();
        void Tab(string name, int headerRow, string[] headers, params string[][] rows)
        {
            var ws = wb.AddWorksheet(name);
            if (headerRow > 1) ws.Cell(1, 1).Value = $"Made-up {name} export";
            for (var c = 0; c < headers.Length; c++) ws.Cell(headerRow, c + 1).Value = headers[c];
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(headerRow + 1 + r, c + 1).Value = rows[r][c];
        }
        Tab("CSA", 1, ["Part Number", "Description", "Material"], [$"CS-{k}-1", desc, material], [$"CS-{k}-2", desc, material]);
        Tab("Fuel Pump", 3, ["Part No", "Desc"], [$"FP-{k}-1", desc], [$"DUP-{k}", "Made-up pump"]);
        Tab("Fuel Injector", 1, ["Item", "Description"], [$"FI-{k}-1", desc], [$"DUP-{k}", "Made-up injector"]);
        Tab("Coil Spring", 2, ["Part Number", "UOM"], [$"CO-{k}-1", "EA"], [$"CO-{k}-2", "Bad unit"]);
        Tab(runtimeFamily, 1, ["Part Number", "Description"], [$"RT-{k}-1", desc]);
        Tab("Notes", 1, ["Part Number", "Description"], [$"NO-{k}-1", "Should not import"]);
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    async Task<(Guid Site, ApiClient Eng, string K, string Runtime)> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var admin = await app.AdminAsync();
        var runtime = "Made-up Family " + k;
        await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "MU_" + k, label = runtime, isActive = true, isDefault = false });
        return (site, eng, k, runtime);
    }

    [Fact]
    public async Task Five_family_tabs_are_read_with_their_own_headings_an_unmatched_tab_is_skipped_and_problems_name_the_tab()
    {
        var (site, eng, k, runtime) = await SetupAsync();
        var t = await Read(eng, Workbook(k, runtime));
        var tabs = t.GetProperty("tabs").EnumerateArray().ToList();
        Assert.Equal(["CSA", "Fuel Pump", "Fuel Injector", "Coil Spring", runtime], tabs.Select(x => x.Str("name")!).ToList());
        Assert.Equal("Complete Strut Assembly (CSA)", tabs[0].Str("family"));
        Assert.Equal(runtime, tabs[4].Str("family")); // a family added at runtime is matched
        Assert.Equal(3, tabs[1].GetProperty("headerRow").GetInt32()); // header row found per tab
        Assert.Equal(0, tabs[2].GetProperty("mapping").GetProperty("partNumber").GetInt32());
        Assert.Contains(t.GetProperty("skippedTabs").EnumerateArray(), x => x.GetString()!.Contains("Tab \"Notes\" was skipped"));

        var plan = await (await eng.PostOkAsync($"{P}/preview", Request(t, site))).JsonAsync();
        var counts = plan.GetProperty("tabs").EnumerateArray().ToDictionary(x => x.Str("tab")!);
        Assert.Equal(2, counts["CSA"].GetProperty("add").GetInt32());
        Assert.Equal(1, counts["Fuel Injector"].GetProperty("skip").GetInt32());
        Assert.Equal(1, counts["Coil Spring"].GetProperty("errors").GetInt32());
        Assert.Contains(plan.GetProperty("skippedTabs").EnumerateArray(), x => x.GetString()!.Contains("Notes"));
        var rows = plan.GetProperty("rows").EnumerateArray().ToList();
        var dup = rows.Single(r => r.Str("tab") == "Fuel Injector" && r.Str("partNumber") == $"DUP-{k}");
        Assert.Equal("Skip", dup.Str("action"));
        Assert.Equal(3, dup.GetProperty("row").GetInt32());
        Assert.Contains("Tab Fuel Pump, row 5", dup.GetProperty("messages")[0].GetString()); // duplicates found across tabs
        Assert.Equal("Error", rows.Single(r => r.Str("partNumber") == $"CO-{k}-2").Str("action"));

        var done = await (await eng.PostOkAsync(P, Request(t, site))).JsonAsync();
        Assert.Equal(7, done.GetProperty("add").GetInt32());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.Contains(k)).ToListAsync();
        Assert.Equal("Complete Strut Assembly (CSA)", parts.Single(p => p.PartNumber == $"CS-{k}-1").ProductFamily);
        Assert.Equal("Fuel Pump", parts.Single(p => p.PartNumber == $"DUP-{k}").ProductFamily);
        Assert.Equal(runtime, parts.Single(p => p.PartNumber == $"RT-{k}-1").ProductFamily);
        Assert.DoesNotContain(parts, p => p.PartNumber.StartsWith("NO-"));
        var audit = await db.AuditEntries.Where(a => a.EntityType == "PartsImport" && a.Description.Contains("made-up-families.xlsx")).OrderByDescending(a => a.OccurredAt).Select(a => a.Description).FirstAsync();
        Assert.Contains("Tabs: CSA (Complete Strut Assembly (CSA)), Fuel Pump (Fuel Pump)", audit);
        Assert.Contains(runtime, audit);
        Assert.Contains("Notes", audit);
    }

    [Fact]
    public async Task Re_import_fills_blanks_only_and_never_overwrites()
    {
        var (site, eng, k, runtime) = await SetupAsync();
        await eng.PostOkAsync(P, Request(await Read(eng, Workbook(k, runtime)), site));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Parts.SingleAsync(x => x.PartNumber == $"CS-{k}-1");
            p.Description = "Edited by hand"; p.Material = null;
            await db.SaveChangesAsync();
        }
        var t = await Read(eng, Workbook(k, runtime, desc: "Changed in the file", material: "Made-up aluminium"));
        var plan = await (await eng.PostOkAsync(P, Request(t, site))).JsonAsync();
        Assert.Equal(0, plan.GetProperty("add").GetInt32());
        Assert.True(plan.GetProperty("update").GetInt32() >= 5);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Parts.AsNoTracking().SingleAsync(x => x.PartNumber == $"CS-{k}-1");
            Assert.Equal("Edited by hand", p.Description); // kept
            Assert.Equal("Made-up aluminium", p.Material);  // blank filled
            Assert.Equal("Made-up description", (await db.Parts.AsNoTracking().SingleAsync(x => x.PartNumber == $"FP-{k}-1")).Description);
            Assert.Contains(await db.AuditEntries.Where(a => a.EntityType == "Part" && a.Action == "Import" && a.Description.Contains($"CS-{k}-1")).Select(a => a.Description).ToListAsync(),
                d => d.Contains("(Tab CSA, row 2)"));
        }
    }
}
