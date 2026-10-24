using Sequfloh.Application.Abstractions;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Crew time (unnamed workers clocked together), backdated and bulk entry, and saved chargeback reports. Made-up data only.</summary>
[Collection("api")]
public sealed class CrewChargebackTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Lead = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime,
        Permissions.SequflohManageChargebacks, Permissions.SequflohViewReports];
    static readonly string[] Plain = [Permissions.SequflohAccess, Permissions.SequflohViewReports];

    async Task<(Guid Site, Guid Id, ApiClient Lead, ApiClient Plain, Guid PlainId)> JobAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, lead) = await app.CreateUserAsync(Lead, site);
        var (plainId, plain) = await app.CreateUserAsync(Plain, site);
        Guid wi;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var w = new SequflohWorkInstruction { Type = WorkInstructionType.Inspection, Title = "Made-up rebox inspection", Number = "CWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
                Status = WorkInstructionStatus.Released };
            w.FamilyId = w.Id;
            db.SequflohWorkInstructions.Add(w);
            await db.SaveChangesAsync();
            wi = w.Id;
        }
        var id = (await (await lead.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        return (site, id, lead, plain, plainId);
    }

    static SequflohCrewSession Session(int crew, DateTimeOffset start, DateTimeOffset stop, long pausedSeconds) =>
        new() { CrewSize = crew, StartedAt = start, StoppedAt = stop, PausedSeconds = pausedSeconds };

    [Fact]
    public void Labor_hours_are_elapsed_minus_paused_times_the_crew_of_each_session()
    {
        var d = new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero);
        var today = Session(3, d, d.AddHours(8.5), 30 * 60);              // 8.5 h elapsed, 30 min lunch -> 8 h x 3 = 24
        var tomorrow = Session(2, d.AddDays(1), d.AddDays(1).AddHours(4), 0); // 4 h x 2 = 8
        Assert.Equal(8.5 * 3600, SequflohCrewService.Elapsed(today, d));
        Assert.Equal(8 * 3600, SequflohCrewService.Worked(today, d));
        Assert.Equal(24 * 3600, SequflohCrewService.Labor(today, d));
        Assert.Equal(32 * 3600, SequflohCrewService.Labor(today, d) + SequflohCrewService.Labor(tomorrow, d));
        // a running, paused session counts the open pause as paused time
        var live = new SequflohCrewSession { CrewSize = 4, StartedAt = d, PausedAt = d.AddHours(2) };
        Assert.Equal(2 * 3600 * 4, SequflohCrewService.Labor(live, d.AddHours(3)));
    }

    [Fact]
    public void Bulk_entry_skips_weekends_and_three_workers_ten_days_eight_hours_is_240()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var x = new CrewBulkInput(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 16), 3, 8, "07:00");
        var plan = SequflohCrewService.Plan(x, zone);
        Assert.Equal(10, plan.Count);
        Assert.DoesNotContain(plan, p => p.Start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(240m, plan.Sum(p => (decimal)(p.Stop - p.Start).TotalHours * p.CrewSize));
        var withLunch = SequflohCrewService.Plan(x with { LunchMinutes = 30 }, zone);
        Assert.All(withLunch, p => Assert.Equal(8.5, (p.Stop - p.Start).TotalHours));
        Assert.Equal(12, SequflohCrewService.Plan(x with { SkipWeekends = false, To = new DateOnly(2026, 10, 16) }, zone).Count);
        Assert.Throws<AppException>(() => SequflohCrewService.Plan(x with { CrewSize = 0 }, zone));
        Assert.Throws<AppException>(() => SequflohCrewService.Plan(x with { HoursPerDay = -1 }, zone));
    }

    [Fact]
    public async Task Bulk_backdate_through_the_api_gives_240_labor_hours_and_is_audited()
    {
        var (_, id, lead, _, _) = await JobAsync();
        var body = new { from = "2026-10-05", to = "2026-10-16", crewSize = 3, hoursPerDay = 8, startTime = "07:00" };
        var preview = await (await lead.PostOkAsync($"{U}/{id}/crew/bulk?preview=true", body)).JsonAsync();
        Assert.Equal(10, preview.Int("days"));
        Assert.Equal("240.00", preview.Str("laborHours"));
        Assert.Equal(0, (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("sessions").GetArrayLength()); // preview saves nothing
        await lead.PostOkAsync($"{U}/{id}/crew/bulk", body);
        var s = await lead.GetElAsync($"{U}/{id}/crew");
        Assert.Equal(240 * 3600, s.GetProperty("crewLaborSeconds").GetInt64());
        Assert.Equal(10, s.GetProperty("sessions").GetArrayLength());
        // running it again overlaps the same days and is refused
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{U}/{id}/crew/bulk", body)).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.EntityId == id && e.Action == "CrewTimeBulkAdded");
        Assert.Contains("240.00 labor hours", a.Description);
        Assert.NotNull(a.UserId);
    }

    [Fact]
    public async Task Backdated_entries_validate_times_and_edits_are_audited_with_before_and_after()
    {
        var (_, id, lead, _, _) = await JobAsync();
        var start = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero);
        object Entry(int crew, DateTimeOffset s, DateTimeOffset e, object[]? pauses = null) => new { crewSize = crew, start = s, stop = e, pauses = pauses ?? [] };
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/sessions", Entry(3, start, start.AddHours(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/sessions", Entry(-2, start, start.AddHours(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/sessions", Entry(3, start, start.AddHours(8),
            [new { from = start.AddHours(1), to = start.AddHours(3) }, new { from = start.AddHours(2), to = start.AddHours(4) }]))).StatusCode); // pauses overlap
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/sessions", Entry(3, start, start.AddHours(8),
            [new { from = start.AddHours(-1), to = start.AddHours(1) }]))).StatusCode); // pause outside the session
        var s = await (await lead.PostOkAsync($"{U}/{id}/crew/sessions", Entry(3, start, start.AddHours(8.5), [new { from = start.AddHours(4), to = start.AddHours(4.5) }]))).JsonAsync();
        Assert.Equal(24 * 3600, s.GetProperty("crewLaborSeconds").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{U}/{id}/crew/sessions", Entry(2, start.AddHours(2), start.AddHours(3)))).StatusCode); // overlaps
        var sid = s.GetProperty("sessions")[0].Id();
        Assert.True((await lead.PutAsync($"{U}/crew/sessions/{sid}", Entry(2, start, start.AddHours(8.5), [new { from = start.AddHours(4), to = start.AddHours(4.5) }]))).IsSuccessStatusCode);
        Assert.Equal(16 * 3600, (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("crewLaborSeconds").GetInt64());
        Assert.Equal(HttpStatusCode.NoContent, (await lead.DeleteAsync($"{U}/crew/sessions/{sid}")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var trail = await db.AuditEntries.AsNoTracking().Where(e => e.EntityId == id && e.Action.StartsWith("CrewTime")).OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["CrewTimeAdded", "CrewTimeEdited", "CrewTimeDeleted"], trail.Select(e => e.Action));
        var edit = trail[1];
        Assert.Contains("3 workers", edit.Description); Assert.Contains("2 workers", edit.Description);
        Assert.Contains("\"before\"", edit.Changes); Assert.Contains("\"after\"", edit.Changes);
        Assert.Contains("24.00 labor hours", edit.Changes); Assert.Contains("16.00 labor hours", edit.Changes);
    }

    [Fact]
    public async Task Crew_time_and_chargebacks_need_their_permissions()
    {
        var (_, id, lead, plain, _) = await JobAsync();
        var start = new DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/crew/sessions", new { crewSize = 3, start, stop = start.AddHours(8) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/crew/start", new { crewSize = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/crew/bulk", new { from = "2026-10-05", to = "2026-10-06", crewSize = 3, hoursPerDay = 8 })).StatusCode);
        var view = await plain.GetElAsync($"{U}/{id}/crew");
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        Assert.True((await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync(C, new { workOrderNumber = "WO-MADEUP-1" })).StatusCode);
        var rep = await (await lead.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-1" })).JsonAsync();
        Assert.True((await plain.GetAsync($"{C}/{rep.Id()}")).IsSuccessStatusCode); // reports access can read
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync($"{C}/{rep.Id()}")).StatusCode);
    }

    [Fact]
    public async Task Registered_people_keep_their_own_clock_alongside_the_crew_and_the_report_shows_both()
    {
        var (_, id, lead, plain, plainId) = await JobAsync();
        // a registered person still clocks on and off the normal way while the crew runs
        await lead.PostOkAsync($"{U}/{id}/crew/start", new { crewSize = 3 });
        await plain.PostOkAsync($"{U}/{id}/time/start");
        await lead.PostOkAsync($"{U}/{id}/crew/pause");
        await lead.PostOkAsync($"{U}/{id}/crew/continue");
        await plain.PostOkAsync($"{U}/{id}/time/stop");
        await lead.PostOkAsync($"{U}/{id}/crew/stop");
        Assert.Single((await plain.GetElAsync($"{U}/{id}/time")).GetProperty("people").EnumerateArray());
        // past time for both: crew of 3 for 8 h on two days, and the registered person 4 h
        var day = new DateTimeOffset(2026, 10, 12, 11, 0, 0, TimeSpan.Zero);
        await lead.PostOkAsync($"{U}/{id}/crew/bulk", new { from = "2026-10-12", to = "2026-10-13", crewSize = 3, hoursPerDay = 8, startTime = "07:00" });
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var num = await db.SequflohTrackingRecords.Where(r => r.Id == id).Select(r => r.Number).SingleAsync();
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = num, UserId = plainId, UserName = "Made-up Tech", StartedAt = day, StoppedAt = day.AddHours(4), Seconds = 4 * 3600 });
            await db.SaveChangesAsync();
        }
        var s = await lead.GetElAsync($"{U}/{id}/crew");
        var crewSec = s.GetProperty("crewLaborSeconds").GetInt64();
        var userSec = s.GetProperty("userSeconds").GetInt64();
        Assert.InRange(crewSec, 48 * 3600, 48 * 3600 + 120);
        Assert.InRange(userSec, 4 * 3600, 4 * 3600 + 60);
        Assert.Equal(crewSec + userSec, s.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(1, s.Int("users"));
        Assert.Equal(4, s.Int("personnel")); // crew of 3 plus one registered person

        var pre = await lead.GetElAsync($"{C}/prefill/{id}");
        Assert.Equal(4, pre.Int("totalPersonnel"));
        Assert.Equal(52m, Math.Round(pre.GetProperty("totalHours").GetDecimal()));
        var rep = await (await lead.PostOkAsync(C, new
        {
            workOrderNumber = "WO-MADEUP-2000", trackingRecordId = id, supplier = "Made-up Supplier Co", partNumber = "E99001", taskDescription = "Rebox and inspect made-up pumps",
            result = "Fail", qtyInspected = 2000, qtyBad = 37, totalPersonnel = 5, hourlyRate = 25, dateFrom = "2026-10-12", dateTo = "2026-10-13",
            costLines = new[] { new { description = "Made-up boxes", amount = 120m } },
        })).JsonAsync();
        Assert.Equal(5, rep.Int("totalPersonnel")); // edited on the report
        Assert.Equal(Math.Round(crewSec / 3600m, 2), rep.GetProperty("crewHours").GetDecimal());
        Assert.Equal(Math.Round(userSec / 3600m, 2), rep.GetProperty("userHours").GetDecimal());
        var hours = rep.GetProperty("totalHours").GetDecimal();
        Assert.Equal(Math.Round(hours * 25, 2), rep.GetProperty("laborCost").GetDecimal());
        Assert.Equal(Math.Round(hours * 25, 2) + 120, rep.GetProperty("total").GetDecimal());
        // invalid input
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { workOrderNumber = "WO-X", qtyInspected = 10, qtyBad = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { workOrderNumber = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { workOrderNumber = "WO-X", result = "Maybe" })).StatusCode);

        var pdf = await lead.GetAsync($"{C}/{rep.Id()}/pdf");
        Assert.True(pdf.IsSuccessStatusCode);
        var pdfText = System.Text.Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("%PDF", pdfText);
        // default sections: the job report layout with worker time added
        foreach (var h in new[] { "(Chargeback)", "(Job)", "(Worker time)", "(Crew time sessions)", "(Man hours by technician)", "(Chargeback summary)", "(Sign-off)", "(Crew labor hours)", "(Registered people hours)", "(Combined total)" })
            Assert.Contains(h, pdfText);
        Assert.DoesNotContain("(Detailed time log)", pdfText); // off by default
        var xlsx = await lead.GetAsync($"{C}/{rep.Id()}/xlsx");
        Assert.True(xlsx.IsSuccessStatusCode);
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()));
        var text = string.Join("|", wb.Worksheet(1).CellsUsed().Select(c => c.GetFormattedString()));
        Assert.Contains("WO-MADEUP-2000", text); Assert.Contains("Crew labor hours", text); Assert.Contains("Registered people hours", text); Assert.Contains("Total chargeback", text);
        Assert.Contains("Crew time sessions", text); Assert.Contains("Man hours by technician", text);

        // only the ticked sections are printed and exported, and the choice is saved with the report
        var picked = await (await lead.PostOkAsync(C, new
        {
            workOrderNumber = "WO-MADEUP-2001", trackingRecordId = id, supplier = "Made-up Supplier Co", partNumber = "E99001", notes = "Made-up note text", hourlyRate = 25,
            sections = new[] { "header", "part", "results", "crewSummary", "timeLog" },
        })).JsonAsync();
        Assert.Equal(new string?[] { "header", "part", "results", "crewSummary", "timeLog" }, picked.GetProperty("sections").EnumerateArray().Select(e => e.GetString()).ToArray());
        var p2 = System.Text.Encoding.Latin1.GetString(await (await lead.GetAsync($"{C}/{picked.Id()}/pdf")).Content.ReadAsByteArrayAsync());
        foreach (var h in new[] { "(Chargeback)", "(Job)", "(Worker time)", "(Detailed time log)", "(E99001)" }) Assert.Contains(h, p2);
        foreach (var h in new[] { "(Crew time sessions)", "(Man hours by technician)", "(Chargeback summary)", "(Sign-off)", "(Made-up note text)", "(Work instruction)" }) Assert.DoesNotContain(h, p2);
        using var wb2 = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await (await lead.GetAsync($"{C}/{picked.Id()}/xlsx")).Content.ReadAsByteArrayAsync()));
        var t2 = string.Join("|", wb2.Worksheet(1).CellsUsed().Select(c => c.GetFormattedString()));
        Assert.Contains("Worker time", t2); Assert.Contains("Detailed time log", t2);
        Assert.DoesNotContain("Crew time sessions", t2); Assert.DoesNotContain("Total chargeback", t2); Assert.DoesNotContain("Made-up note text", t2);
        var view = await lead.GetElAsync($"{C}/{picked.Id()}/view");
        Assert.Equal(new string?[] { "header", "job", "crewSummary", "timeLog" }, view.EnumerateArray().Select(b => b.Str("key")).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { workOrderNumber = "WO-X", sections = new[] { "made-up" } })).StatusCode);
        // the original job report still prints as before
        var jobPdf = await lead.GetAsync($"/api/sequfloh/tracking/{id}/job/report/pdf");
        Assert.True(jobPdf.IsSuccessStatusCode);
        var jr = System.Text.Encoding.Latin1.GetString(await jobPdf.Content.ReadAsByteArrayAsync());
        Assert.Contains("(Chargeback summary)", jr); Assert.DoesNotContain("(Worker time)", jr);

        var list = await lead.GetElAsync($"{C}?q=MADEUP-2000");
        Assert.Single(list.EnumerateArray());

        await lead.PutAsync($"{C}/{rep.Id()}", new { workOrderNumber = "WO-MADEUP-2000", trackingRecordId = id, qtyInspected = 2000, qtyBad = 40, totalPersonnel = 5 });
        using var sc = app.Services.CreateScope();
        var d2 = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var upd = await d2.AuditEntries.AsNoTracking().SingleAsync(e => e.EntityId == rep.Id() && e.Action == "ChargebackReportUpdated");
        Assert.Contains("before", upd.Changes!); Assert.Contains("after", upd.Changes!);
        Assert.Contains("37", upd.Changes!); Assert.Contains("40", upd.Changes!);
    }
}
