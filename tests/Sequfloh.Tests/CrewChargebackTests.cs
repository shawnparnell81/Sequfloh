using System.Text.Json;
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

    [Fact]
    public async Task Named_day_workers_have_their_own_clocks_and_are_listed_by_name_on_the_chargeback()
    {
        var (_, id, lead, plain, _) = await JobAsync();
        // nameless crew time keeps working next to the named workers
        var day = DateTimeOffset.UtcNow.AddDays(-2).Date;
        var start = new DateTimeOffset(day.AddHours(13), TimeSpan.Zero);
        await lead.PostOkAsync($"{U}/{id}/crew/sessions", new { crewSize = 2, start, stop = start.AddHours(4) });

        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/workers", new { name = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/crew/workers", new { name = "Made-up Pat" })).StatusCode);
        await lead.PostOkAsync($"{U}/{id}/crew/workers", new { name = "Made-up Pat" });
        await lead.PostOkAsync($"{U}/{id}/crew/workers", new { name = "Made-up Lee" });
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{U}/{id}/crew/workers", new { name = "made-up pat" })).StatusCode);
        var sum = await lead.GetElAsync($"{U}/{id}/crew");
        var pat = sum.GetProperty("workers").EnumerateArray().First(w => w.Str("name") == "Made-up Pat").Id();
        var lee = sum.GetProperty("workers").EnumerateArray().First(w => w.Str("name") == "Made-up Lee").Id();

        // each worker has their own clock, and it can run while the crew and other workers are on
        await lead.PostOkAsync($"{U}/{id}/crew/start", new { crewSize = 1, workerId = pat });
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{U}/{id}/crew/start", new { crewSize = 1, workerId = pat })).StatusCode);
        await lead.PostOkAsync($"{U}/{id}/crew/start", new { crewSize = 1, workerId = lee });
        await lead.PostOkAsync($"{U}/{id}/crew/pause?workerId={pat}", new { });
        await lead.PostOkAsync($"{U}/{id}/crew/continue?workerId={pat}", new { });
        await lead.PostOkAsync($"{U}/{id}/crew/stop?workerId={pat}", new { });
        sum = await lead.GetElAsync($"{U}/{id}/crew");
        Assert.Equal(JsonValueKind.Null, sum.GetProperty("running").ValueKind); // the crew clock is not affected
        Assert.NotEqual(JsonValueKind.Null, sum.GetProperty("workers").EnumerateArray().First(w => w.Id() == lee).GetProperty("running").ValueKind);
        await lead.PostOkAsync($"{U}/{id}/crew/stop?workerId={lee}", new { });

        // entered time: per-worker overlap check, no future end, and the crew's times don't clash with a worker's
        await lead.PostOkAsync($"{U}/{id}/crew/sessions", new { crewSize = 1, workerId = pat, start, stop = start.AddHours(3), pauses = new[] { new { from = start.AddHours(1), to = start.AddHours(1.5) } } });
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{U}/{id}/crew/sessions", new { crewSize = 1, workerId = pat, start = start.AddHours(2), stop = start.AddHours(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/crew/sessions", new { crewSize = 1, workerId = lee, start = DateTimeOffset.UtcNow, stop = DateTimeOffset.UtcNow.AddHours(3) })).StatusCode);
        await lead.PostOkAsync($"{U}/{id}/crew/sessions", new { crewSize = 1, workerId = lee, start, stop = start.AddHours(2) });
        sum = await lead.GetElAsync($"{U}/{id}/crew");
        var leeSession = sum.GetProperty("sessions").EnumerateArray().First(x => x.GetProperty("workerId").ValueKind != JsonValueKind.Null && x.GetProperty("workerId").GetGuid() == lee && x.GetProperty("entered").GetBoolean()).Id();
        Assert.True((await lead.PutAsync($"{U}/crew/sessions/{leeSession}", new { crewSize = 9, start, stop = start.AddHours(1) })).IsSuccessStatusCode);
        sum = await lead.GetElAsync($"{U}/{id}/crew");
        var edited = sum.GetProperty("sessions").EnumerateArray().First(x => x.Id() == leeSession);
        Assert.Equal(1, edited.GetProperty("crewSize").GetInt32()); // a named worker is always one person
        Assert.Equal(3600, edited.GetProperty("laborSeconds").GetInt64());
        Assert.True((await lead.PutAsync($"{U}/crew/workers/{lee}", new { name = "Made-up Lee Ray" })).IsSuccessStatusCode);

        // totals: crew 2 x 4 h = 8, Pat 2.5 h entered (+ seconds live), Lee 1 h (+ seconds live)
        var labor = sum.GetProperty("crewLaborSeconds").GetInt64();
        Assert.InRange(labor, (8 + 2.5 + 1) * 3600, (8 + 2.5 + 1) * 3600 + 120);
        Assert.InRange(sum.GetProperty("dayWorkerSeconds").GetInt64(), 3.5 * 3600, 3.5 * 3600 + 120);
        Assert.Equal(4, sum.GetProperty("personnel").GetInt32()); // crew of 2 plus two named workers

        var rep = await (await lead.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-DW", trackingRecordId = id })).JsonAsync();
        var view = await lead.GetElAsync($"{C}/{rep.Id()}/view");
        var dw = view.EnumerateArray().First(b => b.Str("key") == "dayWorkers");
        var names = dw.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).ToList();
        Assert.Contains("Made-up Pat", names); Assert.Contains("Made-up Lee Ray", names); Assert.Contains("Total", names);
        var summary = view.EnumerateArray().First(b => b.Str("key") == "crewSummary").GetProperty("fields").EnumerateArray().ToDictionary(f => f.Str("label")!, f => f.Str("value")!);
        Assert.StartsWith("8.00", summary["Crew labor hours"]);
        Assert.StartsWith("3.5", summary["Day worker hours"]);
        Assert.StartsWith("11.5", summary["Combined total"]);
        var crewRows = view.EnumerateArray().First(b => b.Str("key") == "crewSessions").GetProperty("rows").GetArrayLength();
        Assert.Equal(2, crewRows); // the nameless session plus the total row

        // removing a worker removes their time and is audited
        Assert.Equal(HttpStatusCode.NoContent, (await lead.DeleteAsync($"{U}/crew/workers/{pat}")).StatusCode);
        sum = await lead.GetElAsync($"{U}/{id}/crew");
        Assert.DoesNotContain(sum.GetProperty("sessions").EnumerateArray(), x => x.GetProperty("workerId").ValueKind != JsonValueKind.Null && x.GetProperty("workerId").GetGuid() == pat);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action + ": " + a.Description).ToListAsync();
        Assert.Contains(audit, a => a.StartsWith("DayWorkerAdded") && a.Contains("Made-up Pat"));
        Assert.Contains(audit, a => a.StartsWith("DayWorkerRenamed") && a.Contains("from Made-up Lee to Made-up Lee Ray"));
        Assert.Contains(audit, a => a.StartsWith("DayWorkerRemoved") && a.Contains("Made-up Pat"));
        Assert.Contains(audit, a => a.StartsWith("CrewStarted") && a.Contains("Made-up Pat clocked on"));
        Assert.Contains(audit, a => a.StartsWith("CrewTimeEdited") && a.Contains("Made-up Lee"));
    }
}
