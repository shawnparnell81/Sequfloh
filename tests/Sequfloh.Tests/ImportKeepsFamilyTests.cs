using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A Parts import never moves an existing part to another product family. Synthetic data only.</summary>
[Collection("api")]
public sealed class ImportKeepsFamilyTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static string Upc()
    {
        var d = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();
        var sum = d.Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        return d + ((10 - sum % 10) % 10);
    }

    static async Task<JsonElement> Read(ApiClient c, string csv)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv)); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "family.csv");
        var r = await c.Http.PostAsync("/api/sequfloh/parts/import/read", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    static object Request(JsonElement t, Guid site, Guid family) => new
    {
        fileName = "family.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = true,
        defaultTypeId = family, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
    };

    [Fact]
    public async Task Updating_an_existing_part_keeps_its_family_and_UPC_and_the_preview_says_so()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fams = (await eng.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().ToDictionary(x => x.Str("label"), x => x.Id());
        var struts = fams["Struts"]; var coil = fams["Coil Spring"];
        var upc = Upc();

        var first = await Read(eng, $"OR #,Description,UPC\nKF-{k}-1,Example front strut,{upc}\n");
        var p1 = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(first, site, struts))).JsonAsync();
        Assert.True(p1.GetProperty("add").GetInt32() == 1, p1.ToString() + first.GetProperty("mapping"));
        await eng.PostOkAsync("/api/sequfloh/parts/import", Request(first, site, struts));

        var second = await Read(eng, $"OR #,Description\nKF-{k}-1,Example front strut updated\nKF-{k}-2,Example new spring\n");
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(second, site, coil))).JsonAsync();
        var rows = plan.GetProperty("rows").EnumerateArray().ToList();
        // a different product line does not update the part that already belongs to Struts
        Assert.Equal("Add", rows[0].Str("action"));
        Assert.Equal("Add", rows[1].Str("action"));
        await eng.PostOkAsync("/api/sequfloh/parts/import", Request(second, site, coil));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var kept = await db.Parts.AsNoTracking().SingleAsync(p => p.PartNumber == $"KF-{k}-1" && p.TypeId == struts);
        Assert.Equal("Struts", kept.ProductFamily);
        Assert.Equal(upc, kept.Upc);
        Assert.Equal("Example front strut", kept.Description);
        Assert.Equal(coil, await db.Parts.AsNoTracking().Where(p => p.PartNumber == $"KF-{k}-2").Select(p => p.TypeId).SingleAsync());
    }

    [Fact]
    public async Task A_part_with_no_family_takes_the_family_from_the_file()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fams = (await eng.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().ToDictionary(x => x.Str("label"), x => x.Id());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parts.Add(new Sequfloh.Domain.MasterData.Part { PartNumber = $"NF-{k}", Name = "Example", SiteId = site });
            await db.SaveChangesAsync();
        }
        var t = await Read(eng, $"OR #,Description\nNF-{k},Example strut\n");
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(t, site, fams["Struts"]))).JsonAsync();
        Assert.Equal("Add", plan.GetProperty("rows")[0].Str("action"));
        await eng.PostOkAsync("/api/sequfloh/parts/import", Request(t, site, fams["Struts"]));
        using var s2 = app.Services.CreateScope();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        var untouched = await db2.Parts.AsNoTracking().SingleAsync(p => p.PartNumber == $"NF-{k}" && p.CustomerId == null);
        Assert.Null(untouched.TypeId);
        var imported = await db2.Parts.AsNoTracking().SingleAsync(p => p.PartNumber == $"NF-{k}" && p.CustomerId != null);
        Assert.Equal(fams["Struts"], imported.TypeId);
    }
}
