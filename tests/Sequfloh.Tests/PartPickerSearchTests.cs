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
}
