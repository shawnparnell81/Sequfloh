using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>The everyday work order print: the normal work order information on one page, without the long history sections. Made-up data only.</summary>
[Collection("api")]
public sealed class WorkOrderPrintTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static int Pages(string pdf) => Regex.Matches(pdf, @"/Type\s*/Page\b").Count;

    [Fact]
    public async Task Work_order_print_is_one_page_with_the_normal_information_and_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask], site);
        var (_, stranger) = await app.CreateUserAsync([Permissions.SequflohAccess], await app.CreateSiteAsync());
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid pump, part;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.SequflohReferenceValues.FirstAsync(v => v.ListKey == "product-families" && v.Code == "FUEL_PUMP");
            var p = new Part { PartNumber = "WOP" + tag, Name = "Example print pump", SiteId = site, Status = MasterStatus.Active, TypeId = f.Id, ProductFamily = f.Label };
            db.Parts.Add(p); await db.SaveChangesAsync(); pump = f.Id; part = p.Id;
        }
        var id = (await (await c.PostOkAsync(T, new { type = "Test", number = "WOP-" + tag, siteId = site, familyId = pump, partId = part })).JsonAsync()).Id();
        await c.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "testPressure", value = "60", unit = "psi" }, new { key = "shutoffPressure", value = "320-360", unit = "psi" } } });
        for (var i = 0; i < 3; i++) await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = (330 + i).ToString() } } });
        for (var i = 1; i <= 5; i++) await c.PostOkAsync($"{T}/{id}/job/notes", new { text = $"Example note {i} for {tag}" });

        var r = await c.GetAsync($"{T}/{id}/print/work-order");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/pdf", r.Content.Headers.ContentType?.MediaType);
        var pdf = Encoding.Latin1.GetString(await r.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, Pages(pdf));
        foreach (var s in new[] { "WOP-" + tag, "Test Work Order", "WOP" + tag, "Example print pump", "Fuel Pump", "Latest job notes", "Example note 5", "Findings / nonconformance", "320-360 psi", "Part 3", "332" })
            Assert.Contains(s, pdf);
        Assert.DoesNotContain("Example note 1 ", pdf); // only the latest three notes
        Assert.DoesNotContain("Task events", pdf);
        Assert.DoesNotContain("Paused time", pdf);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"{T}/{id}/print/work-order")).StatusCode);
        // the full record print is still there
        Assert.Contains("Task events", Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync()));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == id && a.Action == "WorkOrderPrinted" && a.Description!.Contains("WOP-" + tag)));
        }
    }
}
