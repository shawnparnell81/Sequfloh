using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Tests;

/// <summary>The LINYI PN supplier number on parts: import mapping, fill blanks only, search, export and audit, and never a match key. Made-up numbers only.</summary>
[Collection("api")]
public sealed class LinyiPnTests(TestApp app)
{
    const string A = "/api/sequfloh";
    static object Part(string pn, Guid site, Dictionary<string, string?>? attrs = null) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId = (Guid?)null, attributes = attrs,
    };

    [Fact]
    public void Headers_map_to_LINYI_PN_DWG_and_UPC_and_LINYI_is_not_a_number_key()
    {
        var m = SequflohPartsImport.Suggest(["DMA PN", "LINYI PN", " Drawing", "UPC Code", "linyi  pn"]);
        Assert.Equal(0, m["attr:dmaPn"]); Assert.Equal(1, m["attr:linyiPn"]); Assert.Equal(2, m["drawingNumber"]); Assert.Equal(3, m["upc"]);
        Assert.Equal(0, SequflohPartsImport.Suggest(["  Linyi pn "])["attr:linyiPn"]);
        Assert.False(SequflohPartsImport.IsNumberKey("attr:linyiPn"));
        Assert.DoesNotContain("linyiPn", Sequfloh.Api.Controllers.Sequfloh.SequflohScanController.AlternateKeys);
    }

    [Fact]
    public async Task Import_fills_blank_LINYI_PNs_only_never_matches_on_them_and_search_and_export_find_them()
    {
        var site = await app.CreateSiteAsync();
        var eng = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string L(int n) => $"9.ZZ.{tag}.{n:000}";
        var kept = (await (await eng.PostOkAsync($"{A}/parts", Part($"LK-{tag}", site, new() { ["linyiPn"] = L(1) }))).JsonAsync()).Id();
        var blank = (await (await eng.PostOkAsync($"{A}/parts", Part($"LB-{tag}", site))).JsonAsync()).Id();
        // row 1 keeps its value, row 2 fills a blank, row 3 is a new part whose LINYI PN is already on LK (it must not match LK), row 4 has a "/" placeholder
        var csv = $"DMA PN,LINYI PN, Drawing\nLK-{tag},{L(9)},\nLB-{tag},{L(2)},\nLN-{tag},{L(1)},\nLP-{tag},/,\n";
        var form = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); form.Add(file, "file", "p.csv");
        var t = JsonDocument.Parse(await (await eng.Http.PostAsync($"{A}/parts/import/read", form)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, t.GetProperty("mapping").GetProperty("attr:linyiPn").GetInt32());
        Assert.Equal(2, t.GetProperty("mapping").GetProperty("drawingNumber").GetInt32());
        var mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["attr:linyiPn"] = 1, ["drawingNumber"] = 2 };
        var req = new { fileName = "p.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping, siteId = site, updateExisting = true, keepRepeats = false, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" } };
        var pre = await (await eng.PostOkAsync($"{A}/parts/import/preview", req)).JsonAsync();
        Assert.True(pre.GetProperty("add").GetInt32() > 0, pre.GetRawText() + " " + t.GetRawText());
        var plan = await (await eng.PostOkAsync($"{A}/parts/import", req)).JsonAsync();
        var rows = plan.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("row").GetInt32());
        Assert.Contains(rows[1].GetProperty("messages").EnumerateArray(), m => m.GetString() == $"LINYI PN stays {L(1)} (the file has {L(9)}).");
        Assert.Equal("Update", rows[2].GetProperty("action").GetString());
        Assert.Equal("Add", rows[3].GetProperty("action").GetString());
        Assert.Contains(rows[3].GetProperty("messages").EnumerateArray(), m => m.GetString()!.Contains($"already on part LK-{tag}"));
        Assert.Equal("Add", rows[4].GetProperty("action").GetString());

        async Task<string?> LinyiOf(Guid id) { var a = (await eng.GetElAsync($"{A}/parts/{id}")).GetProperty("part").GetProperty("attributes"); return a.TryGetProperty("linyiPn", out var v) ? v.GetString() : null; }
        Assert.Equal(L(1), await LinyiOf(kept));
        Assert.Equal(L(2), await LinyiOf(blank));
        var list = await eng.GetElAsync($"{A}/parts?q={Uri.EscapeDataString(L(2))}&pageSize=50");
        Assert.Equal([$"LB-{tag}"], list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("partNumber").GetString()).ToArray());
        var lp = (await eng.GetElAsync($"{A}/parts?q=LP-{tag}&pageSize=5")).GetProperty("items")[0];
        Assert.DoesNotContain("linyiPn", lp.GetProperty("attributes").GetRawText());
        var export = await (await eng.Http.GetAsync($"{A}/parts/export?q=LB-{tag}")).Content.ReadAsStringAsync();
        Assert.Contains("LINYI PN", export); Assert.Contains(L(2), export);
        // never scanned
        Assert.Empty((await eng.GetElAsync($"{A}/scan?code={Uri.EscapeDataString(L(2))}")).GetProperty("matches").EnumerateArray());

        // editing on the part is audited
        var rv = (await eng.GetElAsync($"{A}/parts/{blank}")).GetProperty("part").GetProperty("rowVersion").GetUInt32();
        var put = await eng.Http.PutAsJsonAsync($"{A}/parts/{blank}", new { part = new { partNumber = $"LB-{tag}", name = "Example part", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "", hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = rv },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
            typeId = (Guid?)null, attributes = new Dictionary<string, string?> { ["linyiPn"] = L(3) } });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var want = $"LB-{tag}: LINYI PN changed from {L(2)} to {L(3)}.";
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == blank && a.Description == want));
    }
}
