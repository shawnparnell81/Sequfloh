using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>An import update never replaces an existing part's real numbers. Synthetic data only.</summary>
[Collection("api")]
public sealed class PartsImportKeepsNumbersTests(TestApp app)
{
    static async Task<JsonElement> Run(ApiClient c, Guid site, string csv, string step)
    {
        var form = new MultipartFormDataContent(); var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); form.Add(file, "file", "p.csv");
        var t = JsonDocument.Parse(await (await c.Http.PostAsync("/api/sequfloh/parts/import/read", form)).Content.ReadAsStringAsync()).RootElement;
        var req = new { fileName = "p.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = true, keepRepeats = false, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" } };
        return await (await c.PostOkAsync($"/api/sequfloh/parts/{step}", req)).JsonAsync();
    }

    [Fact]
    public async Task An_update_matched_on_another_number_keeps_the_part_number_and_existing_numbers()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], site);
        var k = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        await Run(c, site, $"OR #,JH PN,Drawing number\n43GM{k}PS,JH-{k},DWG-{k}\n", "import");
        var csv = $"OR #,JH PN,Drawing number,NAPA US #\nOTHER-{k},JH-{k},DWG2-{k},N-{k}\n";
        var preview = await Run(c, site, csv, "import/preview");
        var msgs = string.Join(" ", preview.GetProperty("rows")[0].GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Update", preview.GetProperty("rows")[0].GetProperty("action").GetString());
        Assert.Contains($"Part number stays 43GM{k}PS", msgs);
        Assert.Contains($"Drawing number stays DWG-{k}", msgs);
        await Run(c, site, csv, "import");

        var found = await c.GetElAsync($"/api/sequfloh/parts?q=43GM{k}PS");
        var p = Assert.Single(found.GetProperty("items").EnumerateArray());
        Assert.Equal($"43GM{k}PS", p.GetProperty("partNumber").GetString());
        Assert.Equal($"DWG-{k}", p.GetProperty("drawingNumber").GetString());
        var attrs = p.GetProperty("attributes").ValueKind == JsonValueKind.String ? JsonDocument.Parse(p.GetProperty("attributes").GetString()!).RootElement : p.GetProperty("attributes");
        Assert.Equal($"43GM{k}PS", attrs.GetProperty("orNumber").GetString());
        Assert.Equal($"N-{k}", attrs.GetProperty("napaUs").GetString());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = p.GetProperty("id").GetGuid();
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.EntityType == "Part").Select(a => a.Description).ToListAsync(), d => d.Contains("OR # stays"));
    }
}
