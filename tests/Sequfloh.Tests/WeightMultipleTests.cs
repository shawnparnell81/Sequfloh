using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Weight work orders can use the fixed Multiple product family choice, like Research. Numbers stay WO-WGH-{yy}-NNN. Made-up data only.</summary>
[Collection("api")]
public sealed class WeightMultipleTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task A_weight_order_can_be_Multiple_with_no_family_link_and_the_same_numbering()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"M{tag}", label = $"Weight mix family {tag}", isActive = true, isDefault = false })).JsonAsync()).Id();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewReports], site);
        var yy = (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Year % 100).ToString("00");

        var both = await c.PostAsync(T, new { type = "Weight", siteId = site, familyId = fam, multipleFamilies = true });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("not both", await both.Content.ReadAsStringAsync());
        var none = await c.PostAsync(T, new { type = "Weight", siteId = site });
        Assert.Contains("or Multiple", await none.Content.ReadAsStringAsync());

        Assert.Matches($"^WO-WGH-{yy}-\\d{{3}}$", (await c.GetElAsync($"{T}/next-number?type=Weight&siteId={site}&multiple=true")).GetProperty("number").GetString()!);
        var id = (await (await c.PostOkAsync(T, new { type = "Weight", siteId = site, multipleFamilies = true, number = (string?)null })).JsonAsync()).Id();
        var one = (await (await c.PostOkAsync(T, new { type = "Weight", siteId = site, familyId = fam, number = (string?)null })).JsonAsync()).Id();
        var rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Matches($"^WO-WGH-{yy}-\\d{{3}}$", rec.GetProperty("number").GetString()!);
        Assert.Equal("Multiple", rec.GetProperty("familyName").GetString());
        Assert.DoesNotContain("\"Multiple\"", (await admin.GetElAsync("/api/sequfloh/reference/product-families")).GetRawText());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var r = await db.SequflohTrackingRecords.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Null(r.FamilyId); Assert.Null(r.PartId);
            Assert.Equal(fam, (await db.SequflohTrackingRecords.AsNoTracking().SingleAsync(x => x.Id == one)).FamilyId);
            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.Action == "TrackingRecordCreated").Select(a => a.Description).SingleAsync();
            Assert.Contains("product family Multiple", audit);
        }
        var print = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        foreach (var url in new[] { $"{T}/{id}/job/report", $"{T}/{id}/job/report/pdf", $"/api/sequfloh/reports/task-performance?type=Weight&site={site}&pageSize=50" })
        {
            var r = await admin.GetAsync(url);
            Assert.True(r.IsSuccessStatusCode, $"{url}: {await r.Content.ReadAsStringAsync()}");
        }
        Assert.Contains("Multiple", (await admin.GetAsync($"{T}/{id}/job/report").Result.Content.ReadAsStringAsync()));
        var found = (await c.GetElAsync($"{T}?type=Weight&siteId={site}&q=Multiple&pageSize=50")).GetProperty("items");
        Assert.Equal(1, found.GetArrayLength());
        Assert.Equal(id, found[0].Id());
    }
}
