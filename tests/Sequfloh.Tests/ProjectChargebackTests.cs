using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A chargeback report on a project links its work orders and totals their time. Made-up data only.</summary>
[Collection("api")]
public sealed class ProjectChargebackTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Mgr = [Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime,
        Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks, Permissions.SequflohRemoveTime];

    sealed record Setup(Guid Site, ApiClient Mgr, Guid Project, string Tag, Guid A, Guid B, Guid X, string ANo, string BNo, string XNo, DateTimeOffset Start);

    async Task<Setup> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (uid, mgr) = await app.CreateUserAsync(Mgr, site);
        var pid = (await (await mgr.PostOkAsync("/api/sequfloh/projects", new { number = $"PCB-{tag}", name = "Made-up project", siteId = site })).JsonAsync()).Id();
        async Task<Guid> Wo(string n) => (await (await mgr.PostOkAsync(T, new { type = "Inspection", siteId = site, number = n, projectId = pid })).JsonAsync()).Id();
        var a = await Wo($"PA-{tag}"); var b = await Wo($"PB-{tag}"); var x = await Wo($"PX-{tag}");
        var start = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        // A: a crew of 2 for 3 h with a 1 h pause (4 labor h) and a registered person 2 h; B: a day worker 3 h with a half hour pause; X: 5 h of crew, left out of the report
        var worker = (await (await mgr.PostOkAsync($"{T}/{b}/crew/workers", new { name = "Made-up Dee" })).JsonAsync()).GetProperty("workers").EnumerateArray().First().Id();
        await mgr.PostOkAsync($"{T}/{b}/crew/sessions", new { crewSize = 1, workerId = worker, start = start.AddDays(2), stop = start.AddDays(2).AddHours(3), pauses = new[] { new { from = start.AddDays(2).AddHours(1), to = start.AddDays(2).AddHours(1.5) } } });
        using (var s = app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a || r.Id == x).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a || r.Id == b || r.Id == x).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, $"MU-{tag}"));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = a, TrackingNumber = $"PA-{tag}", CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(3), PausedSeconds = 3600, Entered = true });
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = x, TrackingNumber = $"PX-{tag}", CrewSize = 1, StartedAt = start, StoppedAt = start.AddHours(5), Entered = true });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = a, TrackingNumber = $"PA-{tag}", UserId = uid, UserName = "Made-up Tech", StartedAt = start, StoppedAt = start.AddHours(2), Seconds = 7200 });
            await db.SaveChangesAsync();
        }
        return new(site, mgr, pid, tag, a, b, x, $"PA-{tag}", $"PB-{tag}", $"PX-{tag}", start);
    }

    [Fact]
    public async Task A_project_report_totals_the_ticked_work_orders_and_shows_each_one()
    {
        var s = await SetupAsync();
        var pre = await (await s.Mgr.PostOkAsync($"{C}/prefill-linked", new { workOrderIds = new[] { s.A, s.B } })).JsonAsync();
        Assert.Equal(8.5m, pre.GetProperty("totalHours").GetDecimal()); // 4 crew + 2 registered + 2.5 day worker
        Assert.Equal(4, pre.GetProperty("personnel").GetInt32()); // crew of 2, one person, one day worker
        Assert.Equal($"MU-{s.Tag}", pre.Str("partNumber"));

        var cb = await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "", hourlyRate = 40m, workOrderIds = new[] { s.A, s.B }, period = new { kind = "Project", projectId = s.Project },
            costLines = new[] { new { description = "Made-up freight", amount = 10m } } })).JsonAsync();
        var id = cb.Id();
        Assert.Equal($"PCB-{s.Tag}", cb.Str("workOrderNumber"));
        Assert.Equal(8.5m, cb.GetProperty("totalHours").GetDecimal());
        Assert.Equal(6.5m, cb.GetProperty("crewHours").GetDecimal());
        Assert.Equal(2m, cb.GetProperty("userHours").GetDecimal());
        Assert.Equal(4, cb.GetProperty("totalPersonnel").GetInt32());
        Assert.Equal("2026-10-05", cb.Str("dateFrom")); Assert.Equal("2026-10-07", cb.Str("dateTo"));
        Assert.Equal(340m, cb.GetProperty("laborCost").GetDecimal());
        Assert.Equal(350m, cb.GetProperty("total").GetDecimal());
        Assert.Equal(2, cb.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.Null(cb.Str("trackingRecordId"));

        var view = (await s.Mgr.GetElAsync($"{C}/{id}/view")).EnumerateArray().ToList();
        var table = view.First(b => b.Str("key") == "linkedWorkOrders");
        var rows = table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToArray()).ToList();
        Assert.Equal([s.ANo, s.BNo, "Total"], rows.Select(r => r[0]));
        Assert.Equal("6.00", rows[0][^1]); Assert.Equal("2.50", rows[1][^1]); Assert.Equal("8.50", rows[2][^1]);
        Assert.DoesNotContain(rows, r => r[0] == s.XNo);
        var job = view.First(b => b.Str("key") == "job").GetProperty("fields").EnumerateArray().ToDictionary(f => f.Str("label")!, f => f.Str("value"));
        Assert.Equal($"2: {s.ANo}, {s.BNo}", job["Linked work orders"]);
        Assert.StartsWith("8.50", job["Total man hours"]);

        var pdf = await s.Mgr.Http.GetAsync($"{C}/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var xl = await s.Mgr.Http.GetAsync($"{C}/{id}/xlsx");
        using (var wb = new XLWorkbook(new MemoryStream(await xl.Content.ReadAsByteArrayAsync())))
        {
            var cells = wb.Worksheet(1).CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Contains(s.ANo, cells); Assert.Contains(s.BNo, cells); Assert.Contains("8.50", cells); Assert.Contains("340.00", cells);
        }

        using var sc = app.Services.CreateScope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.Action == "ChargebackReportCreated").SingleAsync();
        Assert.Equal($"Chargeback report PCB-{s.Tag} created, linking 2 work orders ({s.ANo}, {s.BNo}), 8.50 man hours, 4 personnel", audit.Description);
    }

    [Fact]
    public async Task Saving_again_totals_again_and_changed_time_flags_the_report()
    {
        var s = await SetupAsync();
        var id = (await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "", workOrderIds = new[] { s.A, s.B }, period = new { kind = "Project", projectId = s.Project } })).JsonAsync()).Id();

        // time removed on B flags the report; saving it again with X ticked as well totals again and clears the flag, whatever hours are typed
        var row = (await s.Mgr.GetElAsync($"{T}/{s.B}/time-corrections")).GetProperty("entries").EnumerateArray().First().Id();
        await s.Mgr.PostOkAsync($"{T}/{s.B}/time-corrections/remove", new { crewSessionIds = new[] { row }, reason = "Made-up duplicate" });
        var flagged = await s.Mgr.GetElAsync($"{C}/{id}");
        Assert.Equal(JsonValueKind.String, flagged.GetProperty("timeChangedAt").ValueKind);

        var again = await (await s.Mgr.PutAsync($"{C}/{id}", new { workOrderNumber = $"PCB-{s.Tag}", totalHours = 99m, workOrderIds = new[] { s.A, s.B, s.X }, period = new { kind = "Project", projectId = s.Project } })).JsonAsync();
        Assert.Equal(11m, again.GetProperty("totalHours").GetDecimal()); // 6 on A, 0 left on B, 5 on X
        Assert.Equal(3, again.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("timeChangedAt").ValueKind);
    }

    [Fact]
    public async Task A_single_work_order_report_works_as_before_and_permissions_are_checked_per_work_order()
    {
        var s = await SetupAsync();
        var one = await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-1", trackingRecordId = s.A })).JsonAsync();
        Assert.Equal(6m, one.GetProperty("totalHours").GetDecimal());
        Assert.Equal(s.A, one.GetProperty("trackingRecordId").GetGuid());
        Assert.Equal(0, one.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.DoesNotContain((await s.Mgr.GetElAsync($"{C}/{one.Id()}/view")).EnumerateArray(), b => b.Str("key") == "linkedWorkOrders");

        // the chargeback permission for another site only: refused for these work orders
        var other = await app.CreateSiteAsync();
        var (_, elsewhere) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks], other);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.PostAsync(C, new { workOrderNumber = "X", workOrderIds = new[] { s.A } })).StatusCode);
    }
}
