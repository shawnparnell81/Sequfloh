using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Import matches only inside the chosen customer and product line. A conflicting drawing on that part is held. Synthetic rows only.</summary>
[Collection("api")]
public sealed class CustomerScopedImportTests(TestApp app)
{
    const string P = "/api/sequfloh/parts/import";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static async Task<JsonElement> Read(ApiClient c, string csv)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "customer.csv");
        var r = await c.Http.PostAsync(P + "/read", form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task<(Guid Customer, Guid Line, string CustomerName, string LineName)> Scope(ApiClient c, string customer, string line)
    {
        var customers = await c.GetElAsync("/api/sequfloh/reference/customers");
        var lines = await c.GetElAsync("/api/sequfloh/reference/product-families");
        var cust = customers.EnumerateArray().First(x => x.Str("label") == customer);
        var fam = lines.EnumerateArray().First(x => x.Str("label") == line);
        return (cust.Id(), fam.Id(), customer, line);
    }

    static object Req(JsonElement t, Guid site, Guid customer, Guid line, bool update, bool keep = false) => new
    {
        fileName = "customer.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"),
        siteId = site, customerId = customer, defaultTypeId = line, updateExisting = update, keepRepeats = keep,
        @fixed = new Dictionary<string, string> { ["name"] = "Complete Strut Assembly (CSA)" },
    };

    [Fact]
    public async Task An_import_without_a_customer_and_product_line_is_refused()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var t = await Read(c, "OR #\nX1\n");
        var req = new HttpRequestMessage(HttpMethod.Post, P + "/preview")
        {
            Content = JsonContent.Create(new { fileName = "customer.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = true, @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" } }),
        };
        req.Headers.Add("X-Test-Own-Number", "1");
        var r = await c.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Choose a customer and a product line", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_file_twice_adds_nothing_the_second_time_and_two_customers_stay_separate()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var oreilly = await Scope(c, "O'Reilly", "Complete Strut Assembly (CSA)");
        var napa = await Scope(c, "NAPA US", "Complete Strut Assembly (CSA)");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var csv = $"OR #,DWG #,Position\n{k}L,9214-0800-{k},FL\n{k}R,9214-0800-{k},FR\n";
        var t = await Read(c, csv);
        var first = await (await c.PostOkAsync(P, Req(t, site, oreilly.Customer, oreilly.Line, false))).JsonAsync();
        Assert.Equal(2, first.Int("add"));
        Assert.Equal(0, first.Int("held"));
        var again = await (await c.PostOkAsync(P, Req(t, site, oreilly.Customer, oreilly.Line, true))).JsonAsync();
        Assert.Equal(0, again.Int("add"));
        Assert.Equal(2, again.Int("unchanged"));
        var other = await (await c.PostOkAsync(P, Req(t, site, napa.Customer, napa.Line, false))).JsonAsync();
        Assert.Equal(2, other.Int("add"));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber == k + "L" || p.PartNumber == k + "R").ToListAsync();
        Assert.Equal(4, parts.Count);
        Assert.Equal(2, parts.Count(p => p.CustomerId == oreilly.Customer));
        Assert.Equal(2, parts.Count(p => p.CustomerId == napa.Customer));
        Assert.All(parts, p => Assert.Equal($"9214-0800-{k}", p.DrawingNumber));
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == "Import" && a.Description.Contains("2 unchanged") && a.Description.Contains("O'Reilly / Complete Strut Assembly (CSA)")).OrderByDescending(a => a.OccurredAt).FirstAsync();
        Assert.False(string.IsNullOrWhiteSpace(audit.UserName));
        Assert.Contains("Existing part IDs and links were kept", audit.Description);
        Assert.NotEqual(default, audit.OccurredAt);
        _ = admin;
    }

    [Fact]
    public async Task A_different_drawing_on_the_same_part_is_held_and_a_shared_drawing_is_not()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "O'Reilly", "Complete Strut Assembly (CSA)");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var first = await Read(c, $"OR #,DWG #,Key Application,Description\n{k},9214-1160,Example,{k} notes\n");
        await c.PostOkAsync(P, Req(first, site, scopeIds.Customer, scopeIds.Line, false));
        var clash = await Read(c, $"OR #,DWG #,Key Application,Description\n{k},9214-0127,Changed,{k} notes changed\n");
        var plan = await (await c.PostOkAsync(P + "/preview", Req(clash, site, scopeIds.Customer, scopeIds.Line, true))).JsonAsync();
        var row = plan.GetProperty("rows")[0];
        Assert.Equal("Held", row.Str("action"));
        Assert.Equal("9214-1160", row.Str("saved"));
        Assert.Equal("9214-0127", row.Str("incoming"));
        Assert.Contains("Existing part and links unchanged", row.GetProperty("messages")[0].GetString());
        var denied = await c.PostAsync(P, Req(clash, site, scopeIds.Customer, scopeIds.Line, true));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        using var dbScope = app.Services.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var part = await db.Parts.SingleAsync(p => p.PartNumber == k && p.CustomerId == scopeIds.Customer);
        Assert.Equal("9214-1160", part.DrawingNumber);
        Assert.Equal($"{k} notes", part.Description);
        Assert.Equal("Example", PartAttributes.Read(part.Attributes)["keyApplication"]);
        var csv = await (await c.Http.PostAsJsonAsync(P + "/held.csv", Req(clash, site, scopeIds.Customer, scopeIds.Line, true))).Content.ReadAsStringAsync();
        Assert.Contains(k, csv);
        Assert.Contains("9214-0127", csv);
    }

    [Fact]
    public async Task A_blank_does_not_erase_a_saved_value_and_a_revision_conflict_holds_the_row()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "NAPA Canada", "Struts");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var first = await Read(c, $"OR #,DWG #,Drawing revision,Description\n{k},DWG-{k},C,Keep me\n");
        await c.PostOkAsync(P, Req(first, site, scopeIds.Customer, scopeIds.Line, false));
        var blank = await Read(c, $"OR #,DWG #,Description\n{k},,Still here\n");
        var filled = await (await c.PostOkAsync(P + "/preview", Req(blank, site, scopeIds.Customer, scopeIds.Line, true))).JsonAsync();
        Assert.Equal("Update", filled.GetProperty("rows")[0].Str("action"));
        await c.PostOkAsync(P, Req(blank, site, scopeIds.Customer, scopeIds.Line, true));
        var rev = await Read(c, $"OR #,Drawing revision\n{k},D\n");
        var held = await (await c.PostOkAsync(P + "/preview", Req(rev, site, scopeIds.Customer, scopeIds.Line, true))).JsonAsync();
        Assert.Equal("Held", held.GetProperty("rows")[0].Str("action"));
        Assert.Equal("C", held.GetProperty("rows")[0].Str("saved"));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var part = await db.Parts.SingleAsync(p => p.PartNumber == k && p.CustomerId == scopeIds.Customer);
        Assert.Equal($"DWG-{k}", part.DrawingNumber);
        Assert.Equal("C", part.DrawingRevision);
        Assert.Equal("Still here", part.Description);
    }

    [Fact]
    public async Task A_barcode_collision_is_held_and_position_repeats_stay_separate()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "AutoZone", "Coil Spring");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var upc = Upc();
        var first = await Read(c, $"OR #,Position,UPC\n{k}A,FL,{upc}\n{k}A,FR,\n");
        var added = await (await c.PostOkAsync(P, Req(first, site, scopeIds.Customer, scopeIds.Line, false, keep: true))).JsonAsync();
        Assert.Equal(2, added.Int("add"));
        var clash = await Read(c, $"OR #,UPC\n{k}B,{upc}\n");
        var plan = await (await c.PostOkAsync(P + "/preview", Req(clash, site, scopeIds.Customer, scopeIds.Line, false))).JsonAsync();
        Assert.Equal("Held", plan.GetProperty("rows")[0].Str("action"));
        Assert.Contains(k + "A", plan.GetProperty("rows")[0].GetProperty("messages")[0].GetString());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Parts.CountAsync(p => p.PartNumber == k + "A"));
        Assert.False(await db.Parts.AnyAsync(p => p.PartNumber == k + "B"));
    }

    [Fact]
    public async Task Two_imports_of_the_same_customer_line_do_not_add_the_part_twice()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var other = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "NAPA US", "Fuel Pump");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var t = await Read(c, $"OR #\n{k}\n");
        var req = Req(t, site, scopeIds.Customer, scopeIds.Line, true);
        var both = await Task.WhenAll(c.PostAsync(P, req), other.Client.PostAsync(P, req));
        Assert.All(both, r => Assert.True(r.IsSuccessStatusCode, r.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Parts.CountAsync(p => p.PartNumber == k && p.CustomerId == scopeIds.Customer && p.TypeId == scopeIds.Line));
    }

    [Fact]
    public async Task Confirming_after_the_part_was_saved_does_not_add_it_again()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "O'Reilly", "Fuel Pump");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var t = await Read(c, $"OR #\n{k}\n");
        var preview = await (await c.PostOkAsync(P + "/preview", Req(t, site, scopeIds.Customer, scopeIds.Line, false))).JsonAsync();
        Assert.Equal("Add", preview.GetProperty("rows")[0].Str("action"));
        await c.PostOkAsync(P, Req(t, site, scopeIds.Customer, scopeIds.Line, false));
        var confirmed = await (await c.PostOkAsync(P, Req(t, site, scopeIds.Customer, scopeIds.Line, true))).JsonAsync();
        Assert.Equal(0, confirmed.Int("add"));
        Assert.Equal(1, confirmed.Int("unchanged"));
    }

    [Fact]
    public async Task A_failed_drawing_link_leaves_the_saved_part_and_can_be_tried_again()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var scopeIds = await Scope(c, "O'Reilly", "Fuel Pump");
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var t = await Read(c, $"OR #,DWG #\n{k},DRAW-{k}\n");
        SequflohPartsImport.LinkFault = new InvalidOperationException("link down");
        JsonElement plan;
        try { plan = await (await c.PostOkAsync(P, Req(t, site, scopeIds.Customer, scopeIds.Line, false))).JsonAsync(); }
        finally { SequflohPartsImport.LinkFault = null; }
        Assert.Equal(1, plan.Int("add"));
        Assert.Contains("not completed", plan.Str("linkFailure"));
        var id = plan.GetProperty("savedPartIds")[0].GetGuid();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.Parts.CountAsync(p => p.Id == id));
            var folder = new DrawingFolder { Name = "Links " + k, SiteId = site };
            db.DrawingFolders.Add(folder);
            await db.SaveChangesAsync();
            db.Drawings.Add(new Drawing { FolderId = folder.Id, Title = "DRAW-" + k, DrawingNumber = "DRAW-" + k, CurrentRevision = "A", SiteId = site });
            await db.SaveChangesAsync();
        }
        var retry = await (await c.PostOkAsync(P + "/link-drawings", new { partIds = new[] { id } })).JsonAsync();
        Assert.True(retry.Int("added") >= 1);
        Assert.Equal(JsonValueKind.Null, retry.GetProperty("failure").ValueKind);
        using var end = app.Services.CreateScope();
        var dbEnd = end.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await dbEnd.Parts.CountAsync(p => p.Id == id));
        Assert.True(await dbEnd.DrawingPartLinks.AnyAsync(l => l.PartId == id));
    }

    static string Upc()
    {
        var d = Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString();
        var sum = d.Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        return d + ((10 - sum % 10) % 10);
    }
}
