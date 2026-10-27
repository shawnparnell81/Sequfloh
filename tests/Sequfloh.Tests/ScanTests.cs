using System.Net;
using System.Text.Json;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Api.Controllers.Sequfloh;
using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace Sequfloh.Tests;

/// <summary>Barcode scans: exact part numbers first, exact alternate numbers only as a fallback, exact record numbers, site scope, and Code 128 on printouts. Made-up data only.</summary>
[Collection("api")]
public sealed class ScanTests(TestApp app)
{
    const string A = "/api/sequfloh";
    static object Part(string pn, Guid site, object? attributes = null, string? dwg = null, bool repairable = false) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = repairable, isSerialized = false, isLotControlled = false, drawingNumber = dwg, specification = (string?)null },
        typeId = (Guid?)null, attributes,
    };
    static string[] Parts(JsonElement r) => r.GetProperty("matches").EnumerateArray().Select(x => x.GetProperty("partNumber").GetString()!).ToArray();
    static string[] Records(JsonElement r) => r.GetProperty("records").EnumerateArray().Select(x => x.GetProperty("number").GetString()!).ToArray();

    [Fact]
    public async Task Scans_match_exact_numbers_only_and_respect_site_scope()
    {
        var site = await app.CreateSiteAsync(); var other = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var main = (await (await admin.PostOkAsync($"{A}/parts", Part($"SC-{tag}", site, new { orNumber = $"OR{tag}", dmaPn = $"DMA{tag}", napaPn = $"NP{tag}" }, $"DW{tag}", repairable: true))).JsonAsync()).Id();
        await admin.PostOkAsync($"{A}/parts", Part($"SC-{tag}-2", site, new { jhPn = $"JH{tag}" }));
        await admin.PostOkAsync($"{A}/parts", Part($"SX-{tag}", site, new { dmaPn = $"MULTI{tag}" }));
        await admin.PostOkAsync($"{A}/parts", Part($"SY-{tag}", site, new { jhPn = $"MULTI{tag}" }));
        await admin.PostOkAsync($"{A}/parts", Part($"HID-{tag}", other));
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts], site);
        Task<JsonElement> Scan(ApiClient u, string code, string? only = null) => u.GetElAsync($"{A}/scan?code={Uri.EscapeDataString(code)}{(only is null ? "" : "&only=" + only)}");

        // exact, case-insensitive, trimmed main part number; not a prefix or partial match
        var r = await Scan(c, $"  sc-{tag.ToLowerInvariant()} ");
        Assert.Equal([$"SC-{tag}"], Parts(r)); Assert.Equal("Part number", r.GetProperty("matchedOn").GetString());
        Assert.Empty(Parts(await Scan(c, $"SC-{tag[..4]}")));
        // DWG #, DMA PN and JH PN exact as a fallback; OR # and NAPA never
        Assert.Equal([$"SC-{tag}"], Parts(await Scan(c, $"dw{tag}")));
        Assert.Equal([$"SC-{tag}"], Parts(await Scan(c, $"DMA{tag}")));
        Assert.Equal([$"SC-{tag}-2"], Parts(await Scan(c, $"JH{tag}")));
        Assert.Empty(Parts(await Scan(c, $"OR{tag}")));
        Assert.Empty(Parts(await Scan(c, $"NP{tag}")));
        // several matches come back as a list
        Assert.Equal([$"SX-{tag}", $"SY-{tag}"], Parts(await Scan(c, $"MULTI{tag}")));
        // placeholders are ignored
        foreach (var p in new[] { "/", "N/A", " - ", "0" }) Assert.Empty(Parts(await Scan(editor, p)));
        // parts at a site the user can't see are not found
        Assert.Empty(Parts(await Scan(c, $"HID-{tag}")));
        Assert.Equal([$"HID-{tag}"], Parts(await Scan(admin, $"HID-{tag}")));
        // Add it? only with permission to create parts
        var miss = await Scan(c, $"NEW-{tag}");
        Assert.Empty(Parts(miss)); Assert.False(miss.GetProperty("canAdd").GetBoolean());
        Assert.True((await Scan(editor, $"NEW-{tag}")).GetProperty("canAdd").GetBoolean());

        // record numbers: inspection request, work order and project open by exact number
        var ir = (await (await admin.PostOkAsync($"{A}/inspection-requests", new { siteId = site, partId = main, number = $"INSP-SC{tag}" })).JsonAsync()).Id();
        Assert.Equal([$"INSP-SC{tag}"], Records(await Scan(c, $"insp-sc{tag.ToLowerInvariant()}")));
        Assert.Empty(Records(await Scan(c, $"INSP-SC{tag}", "parts")));
        var fam = (await (await admin.PostOkAsync($"{A}/reference/product-families", new { code = $"S{tag[..4]}", label = $"Scan family {tag}", isActive = true, isDefault = false })).JsonAsync()).Id();
        var wo = (await (await admin.PostOkAsync($"{A}/tracking", new { type = "Weight", siteId = site, familyId = fam, number = (string?)null })).JsonAsync()).Id();
        var woNumber = (await admin.GetElAsync($"{A}/tracking/{wo}")).GetProperty("record").GetProperty("number").GetString()!;
        Assert.Equal([woNumber], Records(await Scan(c, woNumber)));
        var proj = await (await admin.PostOkAsync($"{A}/projects", new { name = "Scan project " + tag })).JsonAsync();
        var projNumber = proj.GetProperty("number").GetString()!;
        Assert.Contains(projNumber, Records(await Scan(c, projNumber)));

        // the part's latest activity lists its work orders and inspection requests, newest first
        var rep = (await (await admin.PostOkAsync($"{A}/inspection-requests", new { siteId = site, partId = main, number = $"INSP-SD{tag}" })).JsonAsync()).Id();
        var act = await admin.GetElAsync($"{A}/parts/{main}/activity");
        var ids = act.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([rep, ir], ids);
        Assert.True(act.GetProperty("canCreateRepair").GetBoolean());
        Assert.False((await c.GetElAsync($"{A}/parts/{main}/activity")).GetProperty("canCreateRepair").GetBoolean());
    }

    [Fact]
    public void Normalize_trims_and_drops_placeholders()
    {
        Assert.Equal("AB-123", SequflohScanController.Normalize("  AB-123\t"));
        Assert.Null(SequflohScanController.Normalize("/"));
        Assert.Null(SequflohScanController.Normalize("tbd"));
        Assert.Null(SequflohScanController.Normalize("   "));
    }

    [Theory]
    [InlineData("WO-REP-26-001")]
    [InlineData("INSP-AB26-014")]
    [InlineData("PRJ-26-0007")]
    public void Printed_barcodes_decode_back_to_the_record_number(string number)
    {
        var bars = Code128.Modules(number);
        // render the bars as a 3-pixel-per-module image and read it back with a Code 128 reader
        const int k = 3, h = 20;
        var px = new byte[bars.Length * k * h];
        for (var y = 0; y < h; y++) for (var i = 0; i < bars.Length * k; i++) px[y * bars.Length * k + i] = bars[i / k] ? (byte)0 : (byte)255;
        var bmp = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(px, bars.Length * k, h, RGBLuminanceSource.BitmapFormat.Gray8)));
        var result = new Code128Reader().decode(bmp, new Dictionary<DecodeHintType, object> { [DecodeHintType.PURE_BARCODE] = true });
        Assert.NotNull(result);
        Assert.Equal(number, result.Text);
        Assert.Equal(BarcodeFormat.CODE_128, result.BarcodeFormat);
    }
}
