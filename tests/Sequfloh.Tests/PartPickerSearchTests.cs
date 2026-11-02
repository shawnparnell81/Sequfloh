using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>The part picker's search finds imported parts by any number, whatever the case. Synthetic data only.</summary>
[Collection("api")]
public sealed class PartPickerSearchTests(TestApp app)
{
    [Fact]
    public async Task Imported_parts_are_found_by_OR_number_and_other_numbers_in_any_case()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], site);
        var k = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var csv = $"OR #,Key Application,JH PN\n43GM{k}PS,Example sedan,JH-{k}\n/,Example van,ZZ{k}PS\n";
        var form = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); form.Add(file, "file", "p.csv");
        var t = JsonDocument.Parse(await (await eng.Http.PostAsync("/api/sequfloh/parts/import/read", form)).Content.ReadAsStringAsync()).RootElement;
        var req = new { fileName = "p.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = false, keepRepeats = false, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" } };
        Assert.Equal(2, (await (await eng.PostOkAsync("/api/sequfloh/parts/import", req)).JsonAsync()).GetProperty("add").GetInt32());
        foreach (var (q, pn) in new[] { ($"43GM{k}PS", $"43GM{k}PS"), ($" 43gm{k.ToLowerInvariant()} ", $"43GM{k}PS"), ($"jh-{k}", $"43GM{k}PS"), ($"ZZ{k}", $"ZZ{k}PS") })
        {
            var r = await eng.GetElAsync($"/api/sequfloh/parts?pageSize=25&sort=partNumber:asc&q={Uri.EscapeDataString(q)}");
            Assert.Contains(r.GetProperty("items").EnumerateArray(), x => x.GetProperty("partNumber").GetString() == pn);
        }
    }

    static readonly Random Rng = new();
    static string NewUpc()
    {
        var body = "2" + string.Concat(Enumerable.Range(0, 10).Select(_ => Rng.Next(10)));
        for (var c = 0; c < 10; c++) if (Sequfloh.Infrastructure.Services.Gtin.CheckDigitOk(body + c)) return body + c;
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task Parts_are_found_by_any_number_on_the_record_in_Part_Master_and_the_top_search()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var upc = NewUpc();
        var attrs = new Dictionary<string, string?> { ["orNumber"] = $"OR{k}X", ["napaPn"] = $"NP{k}T", ["napaUs"] = $"NU{k}", ["napaCa"] = $"NC{k}", ["nissanVa"] = $"NV{k}", ["dmaPn"] = $"DM{k}L", ["cross"] = $"CR{k}S", ["jhPn"] = $"JH{k}W1" };
        var body = new
        {
            part = new { partNumber = $"AN-{k}", name = "Example part", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = $"DW{k}", specification = (string?)null },
            typeId = (Guid?)null, attributes = attrs, upc,
        };
        await admin.PostOkAsync("/api/sequfloh/parts", body);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        // every number on the record, partial and in any case
        var terms = attrs.Values.Select(v => v![..^1].ToLowerInvariant()).Concat([$"dw{k.ToLowerInvariant()}", upc[2..10], $"an-{k.ToLowerInvariant()}"]);
        foreach (var q in terms)
        {
            var r = await viewer.GetElAsync($"/api/sequfloh/parts?pageSize=25&sort=partNumber:asc&q={Uri.EscapeDataString(q)}");
            Assert.Contains(r.GetProperty("items").EnumerateArray(), x => x.GetProperty("partNumber").GetString() == $"AN-{k}");
            var g = await viewer.GetElAsync($"/api/sequfloh/search?q={Uri.EscapeDataString(q)}");
            var parts = g.GetProperty("groups").EnumerateArray().First(x => x.GetProperty("key").GetString() == "parts");
            Assert.Contains(parts.GetProperty("items").EnumerateArray(), x => x.GetProperty("number").GetString() == $"AN-{k}");
        }
    }
}
