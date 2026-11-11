using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Optional brand on a part, from the admin-editable Brands list. Made-up parts only.</summary>
[Collection("api")]
public sealed class BrandTests(TestApp app)
{
    const string S = "/api/sequfloh";
    static readonly string[] Seeded = ["Duralast", "MasterPro", "Proformer", "Import Direct", "Precision", "Dorman", "Spectra Premium", "Monoshock", "UAS", "WD Fuelmaster", "Brakemaster", "RideMaster", "US Proformer Air", "Nissan Air", "Nissan", "MasterPro Monoshock", "Calvert Racing"];

    static async Task<Guid> Family(ApiClient c) =>
        (await c.GetElAsync($"{S}/reference/product-families")).EnumerateArray().First(x => x.Str("label") == "Complete Strut Assembly (CSA)").Id();

    static object Part(string pn, Guid site, Guid typeId, string? description, Dictionary<string, string?>? attributes, Guid? brandId, uint rowVersion = 0) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description, partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, drawingRevision = (string?)null, specification = (string?)null },
        typeId, attributes, brandId,
    };

    [Fact]
    public async Task Brands_are_a_reference_list_and_a_part_can_keep_one()
    {
        var admin = await app.AdminAsync();
        var lists = await admin.GetElAsync($"{S}/reference");
        Assert.Contains(lists.EnumerateArray(), x => x.Str("key") == "brands" && x.Str("label") == "Brands");
        var values = (await admin.GetElAsync($"{S}/reference/brands")).EnumerateArray().Select(x => x.Str("label")).Where(Seeded.Contains).ToList();
        Assert.Equal(Seeded, values);

        var site = await app.CreateSiteAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var pn = "BR-" + tag;
        var duralast = (await admin.GetElAsync($"{S}/reference/brands")).EnumerateArray().First(x => x.Str("label") == "Duralast").Id();
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam, "Keep this description", new Dictionary<string, string?> { ["keyApplication"] = "RideMaster example" }, null))).JsonAsync()).Id();
        var before = (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("brandId").ValueKind);
        var rv = before.GetProperty("rowVersion").GetUInt32();
        var put = await admin.PutAsync($"{S}/parts/{id}", Part(pn, site, fam, "Keep this description", new Dictionary<string, string?> { ["keyApplication"] = "RideMaster example" }, duralast, rv));
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var detail = (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part");
        Assert.Equal(duralast, detail.GetProperty("brandId").GetGuid());
        Assert.Equal("Duralast", detail.Str("brandName"));
        Assert.Equal("Keep this description", detail.Str("description"));
        Assert.Equal("RideMaster example", detail.GetProperty("attributes").Str("keyApplication"));
        var row = (await admin.GetElAsync($"{S}/parts?q={pn}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Duralast", row.Str("brandName"));

        var csv = (await (await admin.Http.GetAsync($"{S}/parts/export?q={pn}")).Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var head = csv.Split("\r\n")[0].Split(',');
        Assert.Equal("Brand", head[5]);
        Assert.Equal("Duralast", csv.Split("\r\n")[1].Split(',')[5]);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditEntries.Where(a => a.EntityId == id && a.Description.Contains("Brand")).Select(a => a.Description).ToListAsync();
            Assert.Contains(audit, d => d.Contains("Brand set to Duralast"));
        }

        var blocked = await admin.DeleteAsync($"{S}/reference/values/{duralast}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(duralast, (await Snapshot(id)).BrandId);
    }

    [Fact]
    public async Task Import_matches_a_brand_by_label_and_reports_an_unknown_brand()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var fuel = (await admin.GetElAsync($"{S}/reference/product-families")).EnumerateArray().First(x => x.Str("label") == "Fuel Pump").Id();
        var oreilly = (await admin.GetElAsync($"{S}/reference/customers")).EnumerateArray().First(x => x.Str("label") == "O'Reilly").Id();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var pn = "IB-" + tag;
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fuel, "Keep this description", new Dictionary<string, string?> { ["keyApplication"] = "RideMaster example" }, null))).JsonAsync()).Id();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Parts.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, oreilly));
        }

        var preview = await (await admin.PostOkAsync($"{S}/parts/import/preview", new
        {
            fileName = "brands.csv",
            headers = new[] { "Part number", "Brand" },
            rows = new[] { new[] { pn, "spectra premium" }, new[] { "BAD-" + tag, "Not A Brand" } },
            mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["brand"] = 1 },
            siteId = site,
            updateExisting = true,
            @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
        })).JsonAsync();
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.Str("partNumber") == pn && r.Str("action") == "Update");
        var bad = rows.Single(r => r.Str("partNumber") == "BAD-" + tag);
        Assert.Equal("Error", bad.Str("action"));
        Assert.Contains("Not A Brand", bad.GetProperty("messages")[0].GetString());

        await admin.PostOkAsync($"{S}/parts/import", new
        {
            fileName = "brands.csv",
            headers = new[] { "Part number", "Brand" },
            rows = new[] { new[] { pn, "Dorman" } },
            mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["brand"] = 1 },
            siteId = site,
            updateExisting = true,
            @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
        });

        var detail = (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part");
        Assert.Equal("Dorman", detail.Str("brandName"));
        Assert.Equal("Keep this description", detail.Str("description"));
        Assert.Equal("RideMaster example", detail.GetProperty("attributes").Str("keyApplication"));
        Assert.Equal(0, await CountParts("BAD-" + tag));
        Assert.DoesNotContain((await admin.GetElAsync($"{S}/reference/brands")).EnumerateArray(), x => x.Str("label") == "Not A Brand");
    }

    [Fact]
    public async Task A_brand_does_not_assign_a_customer_and_unassigned_parts_can_be_filtered_by_brand()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var nissan = (await admin.GetElAsync($"{S}/reference/brands")).EnumerateArray().First(x => x.Str("label") == "Nissan").Id();
        var ride = (await admin.GetElAsync($"{S}/reference/brands")).EnumerateArray().First(x => x.Str("label") == "RideMaster").Id();
        var oreilly = (await admin.GetElAsync($"{S}/reference/customers")).EnumerateArray().First(x => x.Str("label") == "O'Reilly").Id();
        var barePn = "NB-" + tag;
        var otherPn = "NR-" + tag;
        var ownedPn = "NO-" + tag;
        var bare = (await (await admin.PostOkAsync($"{S}/parts", Part(barePn, site, fam, "plain description", new Dictionary<string, string?> { ["keyApplication"] = "kept application" }, nissan))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts", Part(otherPn, site, fam, "other description", null, ride));
        var owned = (await (await admin.PostOkAsync($"{S}/parts", Part(ownedPn, site, fam, "owned description", null, nissan))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts/move-customer", new { mode = "ids", partIds = new[] { owned }, customerId = oreilly, toUnassigned = false });

        var bareDetail = (await admin.GetElAsync($"{S}/parts/{bare}")).GetProperty("part");
        Assert.Equal(JsonValueKind.Null, bareDetail.GetProperty("customerId").ValueKind);
        Assert.Equal("Nissan", bareDetail.Str("brandName"));
        Assert.Equal("plain description", bareDetail.Str("description"));
        Assert.Equal("kept application", bareDetail.GetProperty("attributes").Str("keyApplication"));
        Assert.Equal("Unassigned", (await Row(admin, barePn)).Str("outcome"));

        var unassigned = await admin.GetElAsync($"{S}/parts?customer=unassigned&brand={nissan}&q={tag}&pageSize=50");
        var numbers = unassigned.GetProperty("items").EnumerateArray().Select(x => x.Str("partNumber")).ToList();
        Assert.Equal([barePn], numbers);
        Assert.Equal("Nissan", unassigned.GetProperty("items")[0].Str("brandName"));
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts?customer=unassigned&brand={ride}&q={barePn}")).GetProperty("total").GetInt32());
        var anyCustomer = (await admin.GetElAsync($"{S}/parts?brand={nissan}&q={tag}&pageSize=50")).GetProperty("items").EnumerateArray().Select(x => x.Str("partNumber")).ToList();
        Assert.Contains(barePn, anyCustomer);
        Assert.Contains(ownedPn, anyCustomer);

        await admin.PostOkAsync($"{S}/parts/move-customer", new { mode = "ids", partIds = new[] { bare }, customerId = oreilly, toUnassigned = false });
        var moved = await Snapshot(bare);
        Assert.Equal(oreilly, moved.CustomerId);
        Assert.Equal(nissan, moved.BrandId);
        await admin.PostOkAsync($"{S}/parts/move-customer", new { mode = "ids", partIds = new[] { bare }, toUnassigned = true });
        var back = await Snapshot(bare);
        Assert.Null(back.CustomerId);
        Assert.Equal(nissan, back.BrandId);

        var fuel = (await admin.GetElAsync($"{S}/reference/product-families")).EnumerateArray().First(x => x.Str("label") == "Fuel Pump").Id();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Parts.Where(p => p.Id == bare).ExecuteUpdateAsync(s => s.SetProperty(p => p.TypeId, fuel).SetProperty(p => p.ProductFamily, "Fuel Pump").SetProperty(p => p.CustomerId, oreilly));
        }
        await admin.PostOkAsync($"{S}/parts/import", new
        {
            fileName = "nissan-brand.csv",
            headers = new[] { "Part number", "Brand" },
            rows = new[] { new[] { barePn, "Nissan" } },
            mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["brand"] = 1 },
            siteId = site,
            updateExisting = true,
            @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
        });
        var imported = (await admin.GetElAsync($"{S}/parts/{bare}")).GetProperty("part");
        Assert.Equal("Nissan", imported.Str("brandName"));
        Assert.Equal("O'Reilly", imported.Str("customerName"));
        Assert.NotEqual("Nissan", imported.GetProperty("customerName").GetString());
    }

    static async Task<JsonElement> Row(ApiClient c, string partNumber)
    {
        var preview = await c.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(partNumber)}&pageSize=50");
        return preview.GetProperty("rows").EnumerateArray().First(r => r.Str("partNumber") == partNumber);
    }

    async Task<int> CountParts(string partNumber)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Parts.CountAsync(p => p.PartNumber == partNumber);
    }

    async Task<PartBrand> Snapshot(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Parts.AsNoTracking().Where(p => p.Id == id).Select(p => new PartBrand(p.BrandId, p.CustomerId, p.Description, p.Attributes)).SingleAsync();
    }

    sealed record PartBrand(Guid? BrandId, Guid? CustomerId, string? Description, string? Attributes);
}
