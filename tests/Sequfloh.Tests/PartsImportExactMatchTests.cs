using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Updating by import: the one part with exactly the row's part number and Position wins over other numbers of the row. Made-up parts only.</summary>
[Collection("api")]
public sealed class PartsImportExactMatchTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];
    const string P = "/api/sequfloh/parts/import";

    static byte[] Book(string tab, string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(tab);
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 2, c + 1).SetValue(rows[r][c]);
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    static async Task<(object Req, JsonElement Plan)> Preview(ApiClient c, Guid site, byte[] bytes, bool update, bool keep = false)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "made-up-exact.xlsx");
        var r = await c.Http.PostAsync($"{P}/read?allTabs=true", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        var t = JsonDocument.Parse(body).RootElement;
        object req = new { fileName = "made-up-exact.xlsx", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site,
            updateExisting = update, keepRepeats = keep, tabs = t.GetProperty("tabs"), skippedTabs = t.GetProperty("skippedTabs") };
        return (req, await (await c.PostOkAsync($"{P}/preview", req)).JsonAsync());
    }

    static List<(string Pn, string Action, string Msg)> Rows(JsonElement plan) => plan.GetProperty("rows").EnumerateArray()
        .Select(x => (x.Str("partNumber")!, x.Str("action")!, string.Join(" ", x.GetProperty("messages").EnumerateArray().Select(m => m.GetString())))).ToList();

    async Task<(Guid Site, ApiClient Eng, string K, string Fam)> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var admin = await app.AdminAsync();
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = "Made-up Air " + k;
        await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "MU_" + k, label = fam, isActive = true, isDefault = false });
        var f = (await admin.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(v => v.Str("label") == fam);
        var put = await admin.Http.PutAsJsonAsync($"/api/sequfloh/reference/values/{f.Id()}/shown-fields", new { fields = new[] { "napaPn" } });
        Assert.True(put.IsSuccessStatusCode);
        // an OR part and its NAPA part, a part that is in the file twice with one Position, and a part with no NAPA twin
        var (seed, plan) = await Preview(eng, site, Book(fam, ["Part number", "Description", "Position", "Key Application"],
            [$"8{k}1C", "Made-up OR part", "", "Made-up app 1"], [$"NP9{k}1C", "Made-up NAPA part", "", "Made-up app 1"],
            [$"3{k}D", "Made-up copy one", "Front", "Made-up app 2"], [$"3{k}D", "Made-up copy two", "Front", "Made-up app 3"], [$"NP9{k}3D", "Made-up NAPA part two", "", "Made-up app 2"],
            [$"8{k}2C", "Made-up OR part two", "", "Made-up app 4"]), update: false, keep: true);
        Assert.Equal(6, plan.GetProperty("add").GetInt32());
        await eng.PostOkAsync(P, seed);
        return (site, eng, k, fam);
    }

    async Task<Dictionary<string, string>> Snapshot(string k)
    {
        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Parts.AsNoTracking().Where(p => p.PartNumber.Contains(k)).ToListAsync())
            .ToDictionary(p => p.Id.ToString(), p => $"{p.PartNumber}|{p.Description}|{p.ProductFamily}|{p.Attributes}");
    }

    [Fact]
    public async Task The_exact_part_number_wins_over_a_NAPA_PN_that_is_another_parts_own_number()
    {
        var (site, eng, k, fam) = await SetupAsync();
        var before = await Snapshot(k);
        var (req, plan) = await Preview(eng, site, Book(fam, ["Part number", "Position", "NAPA PN"], [$"8{k}1C", "", $"NP9{k}1C"]), update: true);
        var row = Rows(plan).Single();
        Assert.Equal("Update", row.Action);
        Assert.Equal(0, plan.GetProperty("errors").GetInt32());
        await eng.PostOkAsync(P, req);
        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var or = await db.Parts.AsNoTracking().SingleAsync(p => p.PartNumber == $"8{k}1C");
        Assert.Equal($"NP9{k}1C", PartAttributes.Read(or.Attributes)["napaPn"]);
        Assert.Equal("Made-up OR part", or.Description);
        Assert.Equal("Made-up app 1", PartAttributes.Read(or.Attributes)["keyApplication"]);
        var after = await Snapshot(k);
        Assert.All(before.Where(b => !b.Value.StartsWith($"8{k}1C|")), b => Assert.Equal(b.Value, after[b.Key])); // the NAPA part and the rest are untouched
    }

    [Fact]
    public async Task Without_update_or_with_an_ambiguous_part_number_the_row_still_matches_more_than_one_part()
    {
        var (site, eng, k, fam) = await SetupAsync();
        var before = await Snapshot(k);
        var (_, off) = await Preview(eng, site, Book(fam, ["Part number", "Position", "NAPA PN"], [$"8{k}1C", "", $"NP9{k}1C"]), update: false);
        Assert.Equal("Error", Rows(off).Single().Action);
        // two parts have this part number and Position, so the exact match is ambiguous
        var (_, amb) = await Preview(eng, site, Book(fam, ["Part number", "Position", "NAPA PN"], [$"3{k}D", "Front", $"NP9{k}3D"]), update: true);
        var row = Rows(amb).Single();
        Assert.Equal("Error", row.Action);
        Assert.Contains("Matches more than one part", row.Msg);
        Assert.Equal(before, await Snapshot(k));
    }

    [Fact]
    public async Task Rows_without_a_conflict_match_as_before()
    {
        var (site, eng, k, fam) = await SetupAsync();
        var (_, plan) = await Preview(eng, site, Book(fam, ["Part number", "Position", "NAPA PN"],
            [$"8{k}2C", "", $"NP9{k}2C"],        // part number only: update as before
            [$"X{k}9", "", $"NP9{k}1C"],         // unknown part number, its NAPA PN is one part's number: that part, as before
            [$"N{k}7", "", ""]), update: true);   // nothing known: added
        var rows = Rows(plan);
        Assert.Equal("Update", rows.Single(r => r.Pn == $"8{k}2C").Action);
        Assert.Equal("Update", rows.Single(r => r.Pn == $"X{k}9").Action);
        Assert.Contains($"Part number stays NP9{k}1C", rows.Single(r => r.Pn == $"X{k}9").Msg);
        Assert.Equal("Add", rows.Single(r => r.Pn == $"N{k}7").Action);
    }
}
