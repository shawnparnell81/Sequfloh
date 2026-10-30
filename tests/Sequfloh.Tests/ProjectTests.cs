using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Projects: work orders of any type and inspection requests totalled together. Made-up data only.</summary>
[Collection("api")]
public sealed class ProjectTests(TestApp app)
{
    const string P = "/api/sequfloh/projects";
    const string T = "/api/sequfloh/tracking";
    const string R = "/api/sequfloh/inspection-requests";

    [Fact]
    public async Task Project_totals_cover_every_linked_work_order_and_request()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks], site);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask], site);

        // permission refused without the projects permission
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync(P, new { number = $"PX-{tag}", name = "Nope" })).StatusCode);

        var pid = (await (await mgr.PostOkAsync(P, new { number = $"PRJ-{tag}", name = "Example project", description = "made up", siteId = site })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.PostAsync(P, new { number = $"prj-{tag}", name = "Duplicate" })).StatusCode);

        // work orders of two types picked into the project on create, an inspection request linked and converted
        var wo1 = (await (await plain.PostOkAsync(T, new { type = "Inspection", siteId = site, number = $"PI-{tag}", projectId = pid })).JsonAsync()).Id();
        var wo2 = (await (await plain.PostOkAsync(T, new { type = "Rebox", siteId = site, number = $"PR-{tag}" })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await plain.PutAsync($"{P}/work-orders/{wo2}", new { projectId = pid })).StatusCode);
        var req = (await (await plain.PostOkAsync(R, new { siteId = site, partNumber = $"PN-{tag}", number = $"PQ-{tag}", projectId = pid })).JsonAsync()).Id();
        var conv = await (await plain.PostOkAsync($"{R}/{req}/convert", new { type = "Inspection", number = $"PC-{tag}" })).JsonAsync();
        var rec = await plain.GetElAsync($"{T}/{wo1}");
        Assert.Equal(pid, rec.GetProperty("record").GetProperty("projectId").GetGuid());

        // crew time: 2 people x 3 h on the first, 1 x 4 h on the second; a cost line on the second
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == wo1 || r.Id == wo2).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = wo1, TrackingNumber = $"PI-{tag}", CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(3) });
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = wo2, TrackingNumber = $"PR-{tag}", CrewSize = 1, StartedAt = start, StoppedAt = start.AddHours(4) });
            var r2 = await db.SequflohTrackingRecords.FindAsync(wo2);
            r2!.CostLines = "[{\"description\":\"Boxes\",\"amount\":25.5}]"; r2.QtyGood = 7; r2.QtyBad = 1;
            await db.SaveChangesAsync();
        }

        var d = await mgr.GetElAsync($"{P}/{pid}");
        var tot = d.GetProperty("totals");
        Assert.Equal(3, tot.GetProperty("workOrders").GetInt32());
        Assert.Equal(1, tot.GetProperty("inspectionRequests").GetInt32());
        Assert.Equal(10m, tot.GetProperty("hours").GetDecimal());
        Assert.Equal(25.5m, tot.GetProperty("costLines").GetDecimal());
        Assert.Equal(7, tot.GetProperty("qtyGood").GetInt32());
        var byType = tot.GetProperty("byType").EnumerateArray().ToDictionary(x => x.GetProperty("type").GetString()!, x => x.GetProperty("count").GetInt32());
        Assert.Equal(2, byType["Inspection"]); Assert.Equal(1, byType["Rebox"]);

        // chargeback report scoped to the project
        var cb = (await (await mgr.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = "", period = new { kind = "Project", projectId = pid }, sections = new[] { "sumHours", "sumByType", "sumDetail" } })).JsonAsync()).Id();
        var view = (await mgr.GetElAsync($"/api/sequfloh/chargebacks/{cb}/view")).EnumerateArray().ToList();
        var hours = view.First(b => b.GetProperty("key").GetString() == "sumHours").GetProperty("fields").EnumerateArray().First().GetProperty("value").GetString();
        Assert.StartsWith("10.00", hours);
        Assert.Equal(3, view.First(b => b.GetProperty("key").GetString() == "sumDetail").GetProperty("rows").GetArrayLength());
        Assert.Contains(view, b => b.GetProperty("heading").GetString() == "Project");
        Assert.Equal($"PRJ-{tag}", (await mgr.GetElAsync($"/api/sequfloh/chargebacks/{cb}")).GetProperty("workOrderNumber").GetString());
        var pdf = await mgr.Http.GetAsync($"/api/sequfloh/chargebacks/{cb}/xlsx");
        Assert.Contains($"Chargeback RPT PRJ-{tag}", pdf.Content.Headers.ContentDisposition?.ToString() ?? "");

        // delete blocked while linked; close blocks new links; reopen
        Assert.Equal(HttpStatusCode.Conflict, (await mgr.Http.DeleteAsync($"{P}/{pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PostAsync($"{P}/{pid}/close", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsync(T, new { type = "Rebox", siteId = site, number = $"PZ-{tag}", projectId = pid })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{P}/{pid}/reopen", new { })).StatusCode);

        // an empty project can be deleted; audit entries were written
        var empty = (await (await mgr.PostOkAsync(P, new { number = $"PE-{tag}", name = "Empty" })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.Http.DeleteAsync($"{P}/{empty}")).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actions = db.AuditEntries.Where(a => a.EntityId == pid || a.EntityId == wo1 || a.EntityId == wo2 || a.EntityId == req).Select(a => a.Action).ToList();
            Assert.Contains("ProjectCreated", actions); Assert.Contains("ProjectClosed", actions); Assert.True(actions.Count(a => a == "ProjectLinked") >= 3);
        }
    }

    [Fact]
    public async Task Project_page_lists_every_item_with_status_and_Home_summary_shows_open_projects()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask], site);
        var (_, outsider) = await app.CreateUserAsync([Permissions.SequflohViewDashboard], site);
        var pid = (await (await mgr.PostOkAsync(P, new { number = $"HP-{tag}", name = "Home example", siteId = site })).JsonAsync()).Id();
        var wo = (await (await mgr.PostOkAsync(T, new { type = "Rebox", siteId = site, number = $"HR-{tag}", projectId = pid })).JsonAsync()).Id();
        var req = (await (await mgr.PostOkAsync(R, new { siteId = site, partNumber = $"HN-{tag}", number = $"HQ-{tag}", projectId = pid })).JsonAsync()).Id();
        await mgr.PostOkAsync($"{R}/{req}/convert", new { type = "Inspection", number = $"HC-{tag}" });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == wo).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = wo, TrackingNumber = $"HR-{tag}", CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(1.5) });
            (await db.SequflohTrackingRecords.FindAsync(wo))!.CostLines = "[{\"description\":\"Tape\",\"amount\":12}]";
            await db.SaveChangesAsync();
        }

        var d = await mgr.GetElAsync($"{P}/{pid}");
        var wos = d.GetProperty("workOrders").EnumerateArray().ToDictionary(x => x.GetProperty("number").GetString()!);
        Assert.Equal(2, wos.Count);
        Assert.Equal("Not started", wos[$"HR-{tag}"].GetProperty("statusLabel").GetString());
        Assert.Equal("Rebox", wos[$"HR-{tag}"].GetProperty("typeLabel").GetString());
        Assert.True(wos[$"HR-{tag}"].TryGetProperty("updatedAt", out _));
        Assert.Equal($"HQ-{tag}", wos[$"HC-{tag}"].GetProperty("fromRequest").GetString());
        Assert.Equal(JsonValueKind.Null, wos[$"HR-{tag}"].GetProperty("fromRequest").ValueKind);
        var ir = d.GetProperty("inspectionRequests").EnumerateArray().Single();
        Assert.Equal(wos[$"HC-{tag}"].GetProperty("id").GetGuid(), ir.GetProperty("workOrderId").GetGuid());
        Assert.False(string.IsNullOrEmpty(ir.GetProperty("statusLabel").GetString()));
        Assert.Equal("Not started", ir.GetProperty("workOrderStatusLabel").GetString());

        var sum = (await mgr.GetElAsync($"{P}/open-summary")).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == pid);
        Assert.Equal(3, sum.GetProperty("items").GetInt32());
        Assert.Equal(d.GetProperty("totals").GetProperty("hours").GetDecimal(), sum.GetProperty("hours").GetDecimal());
        Assert.Equal(3m, sum.GetProperty("hours").GetDecimal());
        Assert.Equal(12m, sum.GetProperty("totalCost").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Http.GetAsync($"{P}/open-summary")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PostAsync($"{P}/{pid}/close", new { })).StatusCode);
        Assert.DoesNotContain((await mgr.GetElAsync($"{P}/open-summary")).EnumerateArray(), x => x.GetProperty("id").GetGuid() == pid);
    }
}
