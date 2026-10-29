using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Tests;

/// <summary>Project numbers PRJ-{yy}-{nnn} given automatically, and work orders and inspection requests linked from the project picker. Made-up data only.</summary>
[Collection("api")]
public sealed class ProjectNumberTests(TestApp app)
{
    const string P = "/api/sequfloh/projects";

    [Fact]
    public void Numbers_count_per_eastern_year_and_skip_taken_ones()
    {
        var mid = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("PRJ-26-001", SequflohProjectService.NextNumber([], mid));
        Assert.Equal("PRJ-26-004", SequflohProjectService.NextNumber(["PRJ-26-001", "prj-26-003", "PRJ-25-009", "OWN-1"], mid));
        // 04:30 UTC on Jan 1 is still Dec 31 in New York
        Assert.Equal("PRJ-26-004", SequflohProjectService.NextNumber(["PRJ-26-003"], new DateTimeOffset(2027, 1, 1, 4, 30, 0, TimeSpan.Zero)));
        Assert.Equal("PRJ-27-001", SequflohProjectService.NextNumber(["PRJ-26-003"], new DateTimeOffset(2027, 1, 1, 5, 30, 0, TimeSpan.Zero)));
        Assert.Equal("PRJ-26-1000", SequflohProjectService.NextNumber(["PRJ-26-999"], mid));
    }

    [Fact]
    public async Task A_new_project_gets_the_next_number_and_records_link_to_it()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask], site);
        var yy = (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Year % 100).ToString("00");

        var next = (await mgr.GetElAsync($"{P}/next-number")).GetProperty("number").GetString()!;
        Assert.StartsWith($"PRJ-{yy}-", next);
        var a = await (await mgr.PostOkAsync(P, new { number = (string?)null, name = "Example auto " + tag })).JsonAsync();
        Assert.Equal(next, a.GetProperty("number").GetString());
        var k = int.Parse(next[7..]);
        // a typed number in the sequence is skipped by the next automatic one
        var typed = $"PRJ-{yy}-{k + 1:000}";
        var t = await mgr.PostAsync(P, new { number = typed, name = "Example typed " + tag });
        Assert.True(t.IsSuccessStatusCode, await t.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.PostAsync(P, new { number = typed.ToLowerInvariant(), name = "Duplicate" })).StatusCode);
        var b = await (await mgr.PostOkAsync(P, new { number = "  ", name = "Example auto 2 " + tag })).JsonAsync();
        Assert.Equal($"PRJ-{yy}-{k + 2:000}", b.GetProperty("number").GetString());
        // editing keeps the number unless it is changed
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.PutAsync($"{P}/{a.Id()}", new { number = "", name = "Renamed" })).StatusCode);

        // the picker lists every open project as number and name; picking one links the record
        var list = (await mgr.GetElAsync(P)).EnumerateArray().ToList();
        Assert.Contains(list, p => p.GetProperty("id").GetGuid() == b.Id() && p.GetProperty("number").GetString() == $"PRJ-{yy}-{k + 2:000}");
        var wo = (await (await mgr.PostOkAsync("/api/sequfloh/tracking", new { type = "Production", siteId = site })).JsonAsync()).Id();
        var req = (await (await mgr.PostOkAsync("/api/sequfloh/inspection-requests", new { siteId = site, partNumber = "MADE-UP-" + tag })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"{P}/work-orders/{wo}", new { projectId = b.Id() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"{P}/inspection-requests/{req}", new { projectId = b.Id() })).StatusCode);
        var page = await mgr.GetElAsync($"{P}/{b.Id()}");
        Assert.Equal([wo], page.GetProperty("workOrders").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList());
        Assert.Equal([req], page.GetProperty("inspectionRequests").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList());
        // choosing No clears the link and it is recorded
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync($"{P}/work-orders/{wo}", new { projectId = (Guid?)null })).StatusCode);
        Assert.Empty((await mgr.GetElAsync($"{P}/{b.Id()}")).GetProperty("workOrders").EnumerateArray());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(x => x.EntityId == wo).ToListAsync(), x => x.Description.Contains($"removed from project PRJ-{yy}-{k + 2:000}") && x.UserName.Length > 0);
    }
}
