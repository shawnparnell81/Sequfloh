using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>
/// The lean job report, clocked time, quantity on the Record tab, the View task events gate and its audit, and the legacy creation crew entries.
/// Made-up data only.
/// </summary>
[Collection("api")]
public sealed class LeanJobReportTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Worker = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohViewReports,
        Permissions.SequflohDispatchTask, Permissions.SequflohManageChargebacks];

    static int Pages(string pdf) => System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type /Page\b").Count;

    async Task<(Guid Id, Guid Site, Guid UserId, ApiClient C)> TestOrderAsync()
    {
        var site = await app.CreateSiteAsync();
        var (uid, c) = await app.CreateUserAsync(Worker, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid fam, part;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.SequflohReferenceValues.FirstAsync(v => v.ListKey == "product-families" && v.Code == "FUEL_PUMP");
            var p = new Part { PartNumber = "LR" + tag, Name = "Example lean pump", SiteId = site, Status = MasterStatus.Active, TypeId = f.Id, ProductFamily = f.Label };
            db.Parts.Add(p); await db.SaveChangesAsync(); fam = f.Id; part = p.Id;
        }
        var id = (await (await c.PostOkAsync(T, new { type = "Test", number = "LRT-" + tag, siteId = site, familyId = fam, partId = part, quantity = 3 })).JsonAsync()).Id();
        await c.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "testPressure", value = "60", unit = "psi" },
            new { key = "shutoffPressure", value = "320-360", unit = "psi" }, new { key = "flowRate", value = "≥95", unit = "L/h" } } });
        foreach (var (sp, fr) in new[] { ("340", "96"), ("350", "99"), ("372", "97") })
            await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = sp }, new { key = "flowRate", value = fr } } });
        return (id, site, uid, c);
    }

    async Task AddNamedTimeAsync(Guid id, Guid user, DateTimeOffset from, double hours)
    {
        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var no = await db.SequflohTrackingRecords.Where(r => r.Id == id).Select(r => r.Number).FirstAsync();
        db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = no, UserId = user, UserName = "Example Tech", StartedAt = from, StoppedAt = from.AddHours(hours),
            Seconds = (long)(hours * 3600), Events = $"[{{\"kind\":\"Start\",\"at\":\"{from:O}\"}},{{\"kind\":\"Stop\",\"at\":\"{from.AddHours(hours):O}\"}}]" });
        await db.SaveChangesAsync();
    }

    async Task<List<(string Action, string Description)>> AuditsAsync(Guid id)
    {
        using var s = app.Services.CreateScope();
        return (await s.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking().Where(a => a.EntityId == id).Select(a => new { a.Action, a.Description }).ToListAsync())
            .Select(a => (a.Action, a.Description ?? "")).ToList();
    }

    [Fact]
    public async Task Lean_job_report_for_a_three_part_test_order_is_one_page_with_clocked_time_and_test_results()
    {
        var (id, _, uid, c) = await TestOrderAsync();
        await c.StartAsync(id);
        await AddNamedTimeAsync(id, uid, new DateTimeOffset(2026, 10, 29, 13, 0, 0, TimeSpan.Zero), 1.5);
        await c.PostOkAsync($"{T}/{id}/job/quantity", new { completed = 3 });

        var rec = await c.GetElAsync($"{T}/{id}");
        Assert.Equal(5400, rec.GetProperty("clockedSeconds").GetInt64()); // clocked time only, not the status clock
        Assert.Equal(1, rec.GetProperty("clockedPersonnel").GetInt32());
        Assert.Equal(3, rec.GetProperty("record").GetProperty("quantityCompleted").GetInt32());

        var bytes = await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync("/workspace/shots/lean-report/test-work-order-job-report.pdf", bytes);
        var pdf = Encoding.Latin1.GetString(bytes);
        Assert.Equal(1, Pages(pdf));
        foreach (var s in new[] { "Job report", "Test results", "Part 1", "Part 2", "Part 3", "320-360 psi", "PASS", "FAIL", "Overall test result: FAIL", "Total time", "1h 30m", "Personnel", "3 of 3" })
            Assert.Contains(s, pdf);
        foreach (var s in new[] { "Task events", "Steps", "No steps.", "Validation form", "Not specified.", "Time breakdown", "Job notes", "Findings / nonconformance", "Active time" })
            Assert.DoesNotContain(s, pdf);

        var detail = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print?timeBreakdown=true")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Time breakdown", detail); Assert.Contains("Example Tech", detail);
        Assert.DoesNotContain("Task events", detail);
        // the one-page work order print shows the same clocked time
        Assert.Contains("1h 30m", Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print/work-order")).Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Task_events_need_the_assigned_permission_and_including_them_is_audited()
    {
        var (id, site, uid, c) = await TestOrderAsync();
        await c.StartAsync(id);
        // without View task events: no events in the record, the endpoints and the report option are refused
        var rec = await c.GetElAsync($"{T}/{id}");
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("events").ValueKind);
        Assert.False(rec.GetProperty("canViewTaskEvents").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{T}/{id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"{T}/{id}/print?events=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/sequfloh/execution/events?site={site}")).StatusCode);
        // the seeded Administrator role does not get it automatically
        Assert.False((await (await app.AdminAsync()).GetElAsync($"{T}/{id}")).GetProperty("canViewTaskEvents").GetBoolean());
        // events keep being recorded in the background
        using (var s = app.Services.CreateScope())
            Assert.True(await s.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTaskEvents.AnyAsync(e => e.TrackingRecordId == id && e.EventType == "Start"));

        // an administrator assigns it through a role
        var (_, auditor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewTaskEvents], site);
        var seen = await auditor.GetElAsync($"{T}/{id}");
        Assert.True(seen.GetProperty("canViewTaskEvents").GetBoolean());
        Assert.Contains(seen.GetProperty("events").EnumerateArray(), e => e.Str("eventType") == "Start");
        Assert.Contains((await auditor.GetElAsync($"{T}/{id}/events")).EnumerateArray(), e => e.Str("eventType") == "Start");
        Assert.DoesNotContain("Task events", Encoding.Latin1.GetString(await (await auditor.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync()));
        Assert.DoesNotContain(await AuditsAsync(id), a => a.Action == "TaskEventsIncluded");
        var bytes = await (await auditor.GetAsync($"{T}/{id}/print?events=true")).Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync("/workspace/shots/lean-report/test-work-order-with-task-events.pdf", bytes);
        Assert.Contains("Task events", Encoding.Latin1.GetString(bytes));
        Assert.Contains(await AuditsAsync(id), a => a.Action == "TaskEventsIncluded" && a.Description.Contains("included task events") && a.Description.Contains("job report"));

        // the chargeback job report keeps its detailed time log; generating it with the log writes the same audit entry
        await AddNamedTimeAsync(id, uid, new DateTimeOffset(2026, 10, 29, 13, 0, 0, TimeSpan.Zero), 1);
        var opts = (await c.GetElAsync($"{T}/{id}/job/report")).GetProperty("options");
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(opts.GetRawText())!;
        dict["timeLog"] = JsonSerializer.SerializeToElement(true);
        await c.PutOkAsync($"{T}/{id}/job/report-options", dict);
        var cb = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/job/report/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Detailed time log", cb);
        Assert.Contains(await AuditsAsync(id), a => a.Action == "TaskEventsIncluded" && a.Description.Contains("chargeback job report"));
    }

    [Fact]
    public async Task Legacy_creation_crew_entries_become_the_creators_named_time_once_and_crew_rows_hide_when_crew_time_is_off()
    {
        var (id, _, uid, c) = await TestOrderAsync();
        var (other, _, uid2, c2) = await TestOrderAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        using (var s = app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var w in new[] { id, other })
            {
                var no = await db.SequflohTrackingRecords.Where(r => r.Id == w).Select(r => r.Number).FirstAsync();
                db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = w, TrackingNumber = no, CrewSize = 1, StartedAt = at, StoppedAt = at.AddMinutes(10), Entered = true, Note = "Creating the work order" });
            }
            await db.SaveChangesAsync();
        }
        // the second creator already clocked that time by name: it must not be counted twice
        await AddNamedTimeAsync(other, uid2, at.AddMinutes(-5), 0.5);
        // crew time is No: the old crew row is not listed anywhere
        Assert.Empty((await c.GetElAsync($"{T}/{id}/crew")).GetProperty("sessions").EnumerateArray());
        Assert.DoesNotContain((await c.GetElAsync($"{T}/{id}/time-corrections")).GetProperty("entries").EnumerateArray(), e => e.Str("kind") == "crew");

        using (var s = app.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(Sequfloh.Data.Migrations.CreationCrewToCreator.Sql);

        Assert.Equal(600, (await c.GetElAsync($"{T}/{id}")).GetProperty("clockedSeconds").GetInt64());
        Assert.Equal(1800, (await c2.GetElAsync($"{T}/{other}")).GetProperty("clockedSeconds").GetInt64());
        using (var s = app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.SequflohTimeEntries.Where(e => e.TrackingRecordId == id && e.UserId == uid && e.StartedAt == at).ToListAsync());
            Assert.Single(await db.SequflohTimeEntries.Where(e => e.TrackingRecordId == other).ToListAsync());
            Assert.All(await db.SequflohCrewSessions.IgnoreQueryFilters().Where(x => x.TrackingRecordId == id || x.TrackingRecordId == other).ToListAsync(),
                x => { Assert.NotNull(x.VoidedAt); Assert.Equal("System", x.VoidedBy); Assert.StartsWith("System correction", x.VoidReason); });
        }
        Assert.Contains(await AuditsAsync(id), a => a.Action == "SystemCorrection" && a.Description.Contains("named time of"));
        Assert.Contains(await AuditsAsync(other), a => a.Action == "SystemCorrection" && a.Description.Contains("not added again"));
        // the job report people list shows the creator, not a crew
        Assert.DoesNotContain((await c.GetElAsync($"{T}/{id}/job/report")).GetProperty("people").EnumerateArray(), p => p.Str("name")!.StartsWith("Crew"));
    }
}
