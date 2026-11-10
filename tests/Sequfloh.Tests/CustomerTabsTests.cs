using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Part Master customer tabs, drawing cross-references and search. Synthetic parts only. A shared drawing is a normal link.</summary>
[Collection("api")]
public sealed class CustomerTabsTests(TestApp app)
{
    const string A = "/api/sequfloh";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static object Part(string pn, Guid site, Guid? typeId, string? drawing) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "Example description " + pn, partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = drawing, specification = (string?)null },
        typeId, attributes = new Dictionary<string, string?>(),
    };

    async Task<Guid> AddAsync(ApiClient c, string pn, Guid site, Guid? typeId, string? drawing, Guid? customer)
    {
        var id = (await (await c.PostOkAsync($"{A}/parts", Part(pn, site, typeId, drawing))).JsonAsync()).Id();
        if (customer is null) return id;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Parts.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, customer));
        return id;
    }

    async Task<Guid> CustomerAsync(string label, bool active = true)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new SequflohReferenceValue { ListKey = "customers", Code = label.ToUpperInvariant().Replace(' ', '_')[..Math.Min(40, label.Length)], Label = label, IsActive = active, SortOrder = 900 };
        db.SequflohReferenceValues.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task Customers_show_only_their_own_lines_and_unassigned_parts_stay_listed()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var oreilly = await CustomerAsync($"Shop {tag}");
        var napa = await CustomerAsync($"Counter {tag}");
        var empty = await CustomerAsync($"Empty {tag}");
        var gone = await CustomerAsync($"Gone {tag}");
        var lines = await admin.GetElAsync($"{A}/reference/product-families");
        var fuel = lines.EnumerateArray().First(x => x.Str("label") == "Fuel Pump").Id();
        var struts = lines.EnumerateArray().First(x => x.Str("label") == "Struts").Id();
        await AddAsync(admin, $"FL-{tag}", site, fuel, $"9214-{tag}", oreilly);
        await AddAsync(admin, $"FR-{tag}", site, fuel, $"9214-{tag}", oreilly);
        var napaPart = await AddAsync(admin, $"NC-{tag}", site, struts, $"9214-{tag}", napa);
        var loose = await AddAsync(admin, $"UA-{tag}", site, fuel, null, null);
        await AddAsync(admin, $"OLD-{tag}", site, struts, $"9000-{tag}", gone);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohReferenceValues.Where(v => v.Id == gone).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));
        }

        var tabs = await admin.GetElAsync($"{A}/parts/customer-tabs");
        var customers = tabs.GetProperty("customers").EnumerateArray().ToList();
        Assert.DoesNotContain(customers, c => c.Str("label").Contains("SenSen", StringComparison.OrdinalIgnoreCase));
        var shop = customers.Single(c => c.Str("label") == $"Shop {tag}");
        var counter = customers.Single(c => c.Str("label") == $"Counter {tag}");
        var blank = customers.Single(c => c.Str("label") == $"Empty {tag}");
        var retired = customers.Single(c => c.Str("label") == $"Gone {tag}");
        Assert.Equal(2, shop.GetProperty("count").GetInt32());
        Assert.Equal(["Fuel Pump"], shop.GetProperty("lines").EnumerateArray().Select(x => x.Str("label")).ToArray());
        Assert.Equal(["Struts"], counter.GetProperty("lines").EnumerateArray().Select(x => x.Str("label")).ToArray());
        Assert.Equal(0, blank.GetProperty("count").GetInt32());
        Assert.Empty(blank.GetProperty("lines").EnumerateArray());
        Assert.False(retired.GetProperty("isActive").GetBoolean());
        Assert.True(tabs.GetProperty("unassigned").GetProperty("count").GetInt32() >= 1);

        var page = await admin.GetElAsync($"{A}/parts?customer={oreilly}&partType={fuel}&pageSize=50");
        var rows = page.GetProperty("items").EnumerateArray().ToDictionary(x => x.Str("partNumber"));
        Assert.Equal(2, rows.Count);
        var sold = rows[$"FL-{tag}"].GetProperty("alsoSoldAs").EnumerateArray().ToList();
        var shown = string.Join(" | ", sold.Select(x => x.GetRawText()));
        Assert.True(sold.Any(x => x.Str("partNumber") == $"FR-{tag}" && x.Str("customerName") == $"Shop {tag}"), shown);
        Assert.True(sold.Any(x => x.Str("partNumber") == $"NC-{tag}" && x.Str("customerName") == $"Counter {tag}"), shown);
        Assert.False(rows[$"FL-{tag}"].TryGetProperty("warning", out _));

        var detail = await admin.GetElAsync($"{A}/parts/{napaPart}");
        var refs = detail.GetProperty("crossReferences").EnumerateArray().Select(x => x.Str("partNumber")).OrderBy(x => x).ToArray();
        Assert.Equal([$"FL-{tag}", $"FR-{tag}"], refs);
        Assert.Equal($"Shop {tag}", detail.GetProperty("crossReferences").EnumerateArray().First().Str("customerName"));

        var located = await admin.GetElAsync($"{A}/parts/locate?q=NC-{tag}");
        var hit = located.GetProperty("items").EnumerateArray().First();
        Assert.True(hit.GetProperty("exactPartNumber").GetBoolean());
        Assert.Equal($"Counter {tag}", hit.Str("customerName"));
        Assert.Equal("Struts", hit.Str("line"));
        var alias = await admin.GetElAsync($"{A}/parts/locate?q=9214-{tag}");
        Assert.Contains(alias.GetProperty("items").EnumerateArray(), x => x.Str("partNumber") == $"NC-{tag}" && !x.GetProperty("exactPartNumber").GetBoolean());

        var unassigned = await admin.GetElAsync($"{A}/parts?customer=unassigned&pageSize=50&q=UA-{tag}");
        Assert.Contains(unassigned.GetProperty("items").EnumerateArray(), x => x.Id() == loose);

        var csv = await (await admin.Http.GetAsync($"{A}/parts/export?customer={oreilly}")).Content.ReadAsStringAsync();
        Assert.Contains("Also sold as", csv);
        Assert.Contains($"FL-{tag}", csv);
        Assert.DoesNotContain($"\nNC-{tag},", csv);

        var other = await app.CreateSiteAsync();
        var (_, outsider) = await app.CreateUserAsync([Permissions.SequflohAccess], other);
        var hidden = await outsider.GetElAsync($"{A}/parts/locate?q=NC-{tag}");
        Assert.DoesNotContain(hidden.GetProperty("items").EnumerateArray(), x => x.Str("partNumber") == $"NC-{tag}");
        var outsiderTabs = await outsider.GetElAsync($"{A}/parts/customer-tabs");
        Assert.DoesNotContain(outsiderTabs.GetProperty("customers").EnumerateArray(), c => c.Str("label") == $"Counter {tag}" && c.GetProperty("count").GetInt32() > 0);
    }
}
