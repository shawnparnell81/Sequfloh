using System.Text.Json;

namespace Sequfloh.Tests;

/// <summary>The number picked for a part (NAPA PN, NAPA US #, NAPA CA #, OR # or another number) is the part number saved on the order; the part link stays the same. Made-up data only.</summary>
[Collection("api")]
public sealed class PickedNumberTests(TestApp app)
{
    const string A = "/api/sequfloh";

    [Fact]
    public async Task Orders_keep_the_picked_number_of_the_part_and_ignore_numbers_that_are_not_on_it()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var part = new
        {
            part = new { partNumber = $"PK-{k}", name = "Example picked part", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = $"DW{k}", specification = (string?)null },
            typeId = (Guid?)null, attributes = new Dictionary<string, string?> { ["napaUs"] = $"NU{k}", ["napaCa"] = $"NC{k}", ["orNumber"] = $"OR{k}" },
        };
        var pid = (await (await admin.PostOkAsync($"{A}/parts", part)).JsonAsync()).Id();

        async Task<JsonElement> Wo(string? pn) => (await admin.GetElAsync($"{A}/tracking/{(await (await admin.PostOkAsync($"{A}/tracking", new { type = "Production", siteId = site, partId = pid, partNumber = pn })).JsonAsync()).Id()}")).GetProperty("record");
        // a NAPA US # or NAPA CA # goes on the order with the part's own spelling; the link is the same part
        var wo = await Wo($"nu{k.ToLowerInvariant()}");
        Assert.Equal($"NU{k}", wo.Str("partNumber")); Assert.Equal(pid, wo.GetProperty("partId").GetGuid());
        Assert.Equal($"NC{k}", (await Wo($"NC{k}")).Str("partNumber"));
        Assert.Equal($"OR{k}", (await Wo($"OR{k}")).Str("partNumber"));
        // no number, or one that is not on the part: the Part Master number
        Assert.Equal($"PK-{k}", (await Wo(null)).Str("partNumber"));
        Assert.Equal($"PK-{k}", (await Wo($"ZZ{k}")).Str("partNumber"));

        // inspection request, then the work order made from it, keep the picked number
        var ir = await (await admin.PostOkAsync($"{A}/inspection-requests", new { siteId = site, partId = pid, partNumber = $"NU{k}" })).JsonAsync();
        Assert.Equal($"NU{k}", ir.Str("partNumber"));
        var conv = await (await admin.PostOkAsync($"{A}/inspection-requests/{ir.Id()}/convert", new { type = "Inspection" })).JsonAsync();
        var rec = await admin.GetElAsync($"{A}/tracking/{conv.GetProperty("workOrderId").GetGuid()}");
        Assert.Equal($"NU{k}", rec.GetProperty("record").Str("partNumber"));

        // a work instruction keeps it too
        var wi = await (await admin.PostOkAsync($"{A}/instructions", new { type = "Procedure", title = "Example " + k, siteId = site, partId = pid, partNumber = $"DW{k}" })).JsonAsync();
        Assert.Equal($"DW{k}", (await admin.GetElAsync($"{A}/instructions/{wi.Id()}")).GetProperty("instruction").Str("partNumber"));
    }
}
