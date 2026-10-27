using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Tests;

/// <summary>UPC / Barcode on parts: box-label scans, equivalent forms, one part per barcode, linking an unknown barcode, and the parts import. Made-up barcodes only.</summary>
[Collection("api")]
public sealed class UpcTests(TestApp app)
{
    const string A = "/api/sequfloh";
    static readonly Random Rng = new();
    /// <summary>A made-up UPC-A with a right check digit.</summary>
    static string NewUpc()
    {
        var body = "2" + string.Concat(Enumerable.Range(0, 10).Select(_ => Rng.Next(10)));
        for (var c = 0; c < 10; c++) if (Gtin.CheckDigitOk(body + c)) return body + c;
        throw new InvalidOperationException();
    }
    static object Part(string pn, Guid site, string? upc = null) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId = (Guid?)null, attributes = (object?)null, upc,
    };
    static string[] Parts(JsonElement r) => r.GetProperty("matches").EnumerateArray().Select(x => x.GetProperty("partNumber").GetString()!).ToArray();

    [Fact]
    public void Barcode_forms_share_one_key_and_check_digits_are_checked()
    {
        Assert.True(Gtin.IsBarcode("042100005264")); Assert.False(Gtin.IsBarcode("43GM1000PS")); Assert.False(Gtin.IsBarcode("1234567"));
        Assert.Equal("00042100005264", Gtin.Key("042100005264"));
        Assert.Equal(Gtin.Key("042100005264"), Gtin.Key("0042100005264"));
        Assert.Equal(Gtin.Key("042100005264"), Gtin.Key("00042100005264"));
        Assert.Equal("042100005264", Gtin.ExpandUpcE("04252614"));
        Assert.Equal(Gtin.Key("042100005264"), Gtin.Key("04252614"));
        Assert.True(Gtin.CheckDigitOk("042100005264"));
        Assert.False(Gtin.CheckDigitOk("042100005265"));
    }

    [Fact]
    public async Task Upc_scans_match_only_the_barcode_field_and_unknown_barcodes_are_linked_not_added()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var upc = NewUpc(); var digitsPn = NewUpc(); var loose = NewUpc();
        var main = (await (await admin.PostOkAsync($"{A}/parts", Part($"UP-{tag}", site, upc))).JsonAsync()).Id();
        // a part whose part number happens to be digits: a barcode scan never matches part numbers
        await admin.PostOkAsync($"{A}/parts", Part(digitsPn, site));
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts], site);
        Task<JsonElement> Scan(ApiClient u, string code, string? only = null) => u.GetElAsync($"{A}/scan?code={code}{(only is null ? "" : "&only=" + only)}");

        Assert.Equal(upc, (await viewer.GetElAsync($"{A}/parts/{main}")).GetProperty("part").GetProperty("upc").GetString());
        foreach (var form in new[] { upc, "0" + upc, "00" + upc })
        {
            var r = await Scan(viewer, form);
            Assert.Equal([$"UP-{tag}"], Parts(r)); Assert.True(r.GetProperty("isBarcode").GetBoolean()); Assert.Equal("UPC / Barcode", r.GetProperty("matchedOn").GetString());
        }
        Assert.Equal([$"UP-{tag}"], Parts(await Scan(viewer, upc, "parts")));
        var digits = await Scan(editor, digitsPn);
        Assert.Empty(Parts(digits)); Assert.True(digits.GetProperty("isBarcode").GetBoolean());

        // one part per barcode, in any of its forms; the error names the other part
        var dup = await admin.PostAsync($"{A}/parts", Part($"UQ-{tag}", site, "0" + upc));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains($"UP-{tag}", await dup.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{A}/parts", Part($"UR-{tag}", site, upc[..11] + ((upc[11] - '0' + 1) % 10)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{A}/parts", Part($"UR-{tag}", site, "12AB"))).StatusCode);

        // an unknown barcode is linked to an existing part, never added as a part named after the digits
        var other = (await (await admin.PostOkAsync($"{A}/parts", Part($"UL-{tag}", site))).JsonAsync()).Id();
        var unknown = await Scan(editor, loose);
        Assert.Empty(Parts(unknown)); Assert.True(unknown.GetProperty("isBarcode").GetBoolean()); Assert.True(unknown.GetProperty("canAdd").GetBoolean());
        Assert.False((await Scan(viewer, loose)).GetProperty("canAdd").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"{A}/parts/{other}/upc", new { upc = loose })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await editor.PutAsync($"{A}/parts/{other}/upc", new { upc })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.PutAsync($"{A}/parts/{other}/upc", new { upc = loose })).StatusCode);
        Assert.Equal([$"UL-{tag}"], Parts(await Scan(viewer, loose)));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.AuditEntries.AsNoTracking().Where(x => x.EntityId == other && x.Action == "Update").OrderByDescending(x => x.OccurredAt).FirstAsync();
            Assert.Equal($"UL-{tag}: UPC / Barcode set to {loose}.", a.Description);
            Assert.False(string.IsNullOrEmpty(a.UserName));
            Assert.False(await db.Parts.AnyAsync(x => x.PartNumber == loose));
        }
    }

    [Fact]
    public async Task Import_fills_blank_barcodes_only_and_keeps_barcodes_unique()
    {
        var site = await app.CreateSiteAsync();
        var eng = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var kept = NewUpc(); var filled = NewUpc(); var fresh = NewUpc(); var ignored = NewUpc();
        var withUpc = (await (await eng.PostOkAsync($"{A}/parts", Part($"IK-{tag}", site, kept))).JsonAsync()).Id();
        var blank = (await (await eng.PostOkAsync($"{A}/parts", Part($"IB-{tag}", site))).JsonAsync()).Id();
        var csv = $"Part number,UPC\nIK-{tag},{ignored}\nIB-{tag},{filled}\nIN-{tag},{fresh}\nIT-{tag},{kept}\nIU-{tag},{fresh}\n";
        var form = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); form.Add(file, "file", "p.csv");
        var t = JsonDocument.Parse(await (await eng.Http.PostAsync($"{A}/parts/import/read", form)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, t.GetProperty("mapping").GetProperty("upc").GetInt32());
        var req = new { fileName = "p.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = true, keepRepeats = false, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" } };
        var plan = await (await eng.PostOkAsync($"{A}/parts/import", req)).JsonAsync();
        var rows = plan.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("row").GetInt32());
        Assert.Contains(rows[1].GetProperty("messages").EnumerateArray(), m => m.GetString() == $"UPC / Barcode stays {kept} (the file has {ignored}).");
        Assert.Equal("Error", rows[4].GetProperty("action").GetString());
        Assert.Contains($"IK-{tag}", rows[4].GetProperty("messages")[0].GetString());
        Assert.Equal("Error", rows[5].GetProperty("action").GetString());
        async Task<string?> UpcOf(Guid id) => (await eng.GetElAsync($"{A}/parts/{id}")).GetProperty("part").GetProperty("upc").GetString();
        Assert.Equal(kept, await UpcOf(withUpc));
        Assert.Equal(filled, await UpcOf(blank));
        var scan = await eng.GetElAsync($"{A}/scan?code={fresh}");
        Assert.Equal([$"IN-{tag}"], Parts(scan));
        // the export has the column
        var export = await (await eng.Http.GetAsync($"{A}/parts/export?q=IB-{tag}")).Content.ReadAsStringAsync();
        Assert.Contains("UPC / Barcode", export); Assert.Contains(filled, export);
    }
}
