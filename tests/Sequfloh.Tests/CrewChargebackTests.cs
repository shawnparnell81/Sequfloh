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

    async Task<(Guid Site, Guid Id, ApiClient Lead, ApiClient Plain, Guid PlainId)> JobAsync(bool crewTime = false)
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
        await lead.StartAsync(id);
        if (crewTime) await lead.UseCrewTimeAsync(id);
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
        var (_, id, lead, _, _) = await JobAsync(true);
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
        var (_, id, lead, _, _) = await JobAsync(true);
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
        var sid = s.GetProperty("sessions")[0].Id();
        Assert.True((await lead.PutAsync($"{U}/crew/sessions/{sid}", Entry(2, start, start.AddHours(8.5), [new { from = start.AddHours(4), to = start.AddHours(4.5) }]))).IsSuccessStatusCode);
        Assert.Equal(16 * 3600, (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("crewLaborSeconds").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await lead.DeleteAsync($"{U}/crew/sessions/{sid}")).StatusCode); // time is never deleted
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PutAsync($"{U}/crew/sessions/{sid}", Entry(2, start, start.AddHours(1), [new { from = start, to = start.AddHours(1) }]))).StatusCode); // can't edit to zero
        Assert.Equal(16 * 3600, (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("crewLaborSeconds").GetInt64());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var trail = await db.AuditEntries.AsNoTracking().Where(e => e.EntityId == id && e.Action.StartsWith("CrewTime") && e.Action != "CrewTimeSetting").OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["CrewTimeAdded", "CrewTimeEdited"], trail.Select(e => e.Action));
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
    public async Task Crew_time_and_registered_time_in_older_data_are_both_on_the_report()
    {
        var (_, id, lead, plain, plainId) = await JobAsync(true);
        // while the work order uses crew time, nobody clocks on by name
        await lead.PostOkAsync($"{U}/{id}/crew/start", new { crewSize = 3 });
        var named = await plain.PostAsync($"{U}/{id}/time/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, named.StatusCode);
        Assert.Contains(SequflohCrewService.NamedBlockedMessage, await named.Content.ReadAsStringAsync());
        await lead.PostOkAsync($"{U}/{id}/crew/pause");
        await lead.PostOkAsync($"{U}/{id}/crew/continue");
        await lead.PostOkAsync($"{U}/{id}/crew/stop");
        // past time for both (the registered person's time is older data saved before the crew-time setting): crew of 3 for 8 h on two days, and the registered person 4 h
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
        // nameless crew time is refused while the work order does not use crew time
        var day = DateTimeOffset.UtcNow.AddDays(-2).Date;
        var start = new DateTimeOffset(day.AddHours(13), TimeSpan.Zero);
        var off = await lead.PostAsync($"{U}/{id}/crew/sessions", new { crewSize = 2, start, stop = start.AddHours(4) });
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Contains(SequflohCrewService.CrewOffMessage, await off.Content.ReadAsStringAsync());

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
        // older data saved before the crew-time setting: a nameless crew row next to the named workers, on a work order set to use crew time
        using (var s0 = app.Services.CreateScope())
        {
            var d0 = s0.ServiceProvider.GetRequiredService<AppDbContext>();
            await d0.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            d0.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = id, CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(4), Entered = true });
            await d0.SaveChangesAsync();
        }
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

        // a worker with time can't be removed and their time can't be deleted; it stays on the report
        Assert.Equal(HttpStatusCode.Conflict, (await lead.DeleteAsync($"{U}/crew/workers/{pat}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.DeleteAsync($"{U}/crew/sessions/{leeSession}")).StatusCode);
        sum = await lead.GetElAsync($"{U}/{id}/crew");
        Assert.Contains(sum.GetProperty("sessions").EnumerateArray(), x => x.GetProperty("workerId").ValueKind != JsonValueKind.Null && x.GetProperty("workerId").GetGuid() == pat);
        var rep2 = await (await lead.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-DW2", trackingRecordId = id })).JsonAsync();
        var names2 = (await lead.GetElAsync($"{C}/{rep2.Id()}/view")).EnumerateArray().First(b => b.Str("key") == "dayWorkers").GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).ToList();
        Assert.Contains("Made-up Pat", names2); Assert.Contains("Made-up Lee Ray", names2);
        // a worker with no time (a typo) can be removed
        await lead.PostOkAsync($"{U}/{id}/crew/workers", new { name = "Made-up Typo" });
        var typoId = (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("workers").EnumerateArray().First(w => w.Str("name") == "Made-up Typo").Id();
        Assert.Equal(HttpStatusCode.NoContent, (await lead.DeleteAsync($"{U}/crew/workers/{typoId}")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action + ": " + a.Description).ToListAsync();
        Assert.Contains(audit, a => a.StartsWith("DayWorkerAdded") && a.Contains("Made-up Pat"));
        Assert.Contains(audit, a => a.StartsWith("DayWorkerRenamed") && a.Contains("from Made-up Lee to Made-up Lee Ray"));
        Assert.Contains(audit, a => a.StartsWith("DayWorkerRemoved") && a.Contains("Made-up Typo"));
        Assert.Contains(audit, a => a.StartsWith("CrewStarted") && a.Contains("Made-up Pat clocked on"));
        Assert.Contains(audit, a => a.StartsWith("CrewTimeEdited") && a.Contains("Made-up Lee"));
    }

    [Fact]
    public async Task A_period_report_sums_recorded_time_counts_results_and_pauses_and_exports()
    {
        var (_, id, lead, plain, plainId) = await JobAsync();
        async Task<int> Ver() => (await lead.GetElAsync($"{U}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        await lead.PostOkAsync($"{U}/{id}/actions/pause", new { operationId = Guid.NewGuid(), stateVersion = await Ver(), reason = "Waiting for made-up parts" });
        await lead.PostOkAsync($"{U}/{id}/actions/resume", new { operationId = Guid.NewGuid(), stateVersion = await Ver() });
        var day = new DateTimeOffset(2001, 2, 14, 12, 0, 0, TimeSpan.Zero);
        var tech = "Period Tech " + Guid.NewGuid().ToString("N")[..6];
        var worker = "Day Worker " + Guid.NewGuid().ToString("N")[..6]; // a named day worker with no account
        var late = "Late Tech " + Guid.NewGuid().ToString("N")[..6]; // time on this job, but a month after the period
        var other = "Other Job Tech " + Guid.NewGuid().ToString("N")[..6]; // time in the period, but only on another job
        var (_, otherId, _, _, _) = await JobAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var on = await db.SequflohTrackingRecords.Where(r => r.Id == otherId).Select(r => r.Number).SingleAsync();
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = otherId, TrackingNumber = on, UserId = plainId, UserName = other, StartedAt = new DateTimeOffset(2001, 2, 14, 13, 0, 0, TimeSpan.Zero), Seconds = 1800 });
            await db.SaveChangesAsync();
        }
        await lead.PostOkAsync($"{U}/{id}/crew/workers", new { name = worker });
        var wid = (await lead.GetElAsync($"{U}/{id}/crew")).GetProperty("workers").EnumerateArray().First(w => w.Str("name") == worker).Id();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var num = await db.SequflohTrackingRecords.Where(r => r.Id == id).Select(r => r.Number).SingleAsync();
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = num, UserId = plainId, UserName = tech, StartedAt = day, StoppedAt = day.AddHours(3), Seconds = 3 * 3600 });
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = id, CrewSize = 2, StartedAt = day, StoppedAt = day.AddHours(2) });
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = id, CrewSize = 1, StartedAt = day, StoppedAt = day.AddHours(1.5), WorkerId = wid, WorkerName = worker });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = num, UserId = plainId, UserName = late, StartedAt = day.AddDays(34), StoppedAt = day.AddDays(34).AddHours(1), Seconds = 3600 });
            await db.SaveChangesAsync();
        }
        var sections = new[] { "sumHours", "sumByType", "sumByPerson", "sumCounts", "sumQuality", "sumChargebacks", "sumPauses", "sumDetail", "sumCharts" };
        var rep = await (await lead.PostOkAsync(C, new { workOrderNumber = "CB-WK7 made up", sections, period = new { kind = "Week", from = "2001-02-14", type = "Production" } })).JsonAsync();
        Assert.Equal("CB-WK7 made up", rep.Str("workOrderNumber"));
        Assert.Equal("Week", rep.GetProperty("period").GetProperty("kind").GetString());
        var view = (await lead.GetElAsync($"{C}/{rep.Id()}/view")).EnumerateArray().ToList();
        JsonElement B(string k) => view.Single(b => b.GetProperty("key").GetString() == k);
        string F(string k, string label) => B(k).GetProperty("fields").EnumerateArray().Single(x => x.GetProperty("label").GetString() == label).GetProperty("value").GetString()!;
        Assert.StartsWith("9.00", F("sumHours", "Total hours")); // 3 h signed-in + crew of 2 for 2 h + day worker 1.5 h, plus 0.5 h on the other job
        Assert.Equal("4.00", F("sumHours", "Crew (labor hours = hours × crew size)"));
        var people = B("sumByPerson").GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).ToList();
        Assert.Contains(tech, people); Assert.Contains("Crew", people);
        Assert.Contains(B("sumByType").GetProperty("rows").EnumerateArray(), r => r[0].GetString() == "Production" && r[2].GetString() == "9.00");
        Assert.Contains(B("sumDetail").GetProperty("rows").EnumerateArray(), r => r[11].GetString() == "8.50");
        Assert.Contains(B("sumCharts").GetProperty("rows").EnumerateArray(), r => r[1].GetString() == tech);
        Assert.Equal("0", F("sumCounts", "Opened in the period")); // created today, timed in 2001
        // a person filter keeps only that person's time
        var one = await (await lead.PostOkAsync(C, new { sections, period = new { kind = "Day", from = "2001-02-14", person = tech } })).JsonAsync();
        var v1 = (await lead.GetElAsync($"{C}/{one.Id()}/view")).EnumerateArray().ToList();
        Assert.StartsWith("3.00", v1.Single(b => b.GetProperty("key").GetString() == "sumHours").GetProperty("fields")[0].GetProperty("value").GetString());
        // the person list holds signed-in people and day workers with no account, sorted and once each; picking the day worker filters to their time
        List<string> People(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Empty(People(await lead.GetElAsync($"{C}/people"))); // nothing chosen yet
        // a linked work order: only people with time on that job, at any date
        var onJob = People(await lead.GetElAsync($"{C}/people?trackingRecordId={id}"));
        Assert.Contains(tech, onJob); Assert.Contains(worker, onJob); Assert.Contains(late, onJob); Assert.DoesNotContain(other, onJob);
        // a period: only people with time in it, narrowed by the other filters
        var list = People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14"));
        Assert.Contains(tech, list); Assert.Contains(worker, list); Assert.Contains(other, list); Assert.DoesNotContain(late, list);
        Assert.DoesNotContain(other, People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14&trackingRecordId={id}")));
        Assert.DoesNotContain(tech, People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14&type=Repair")));
        Assert.Contains(tech, People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14&type=Production")));
        Assert.DoesNotContain(tech, People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14&familyId={Guid.NewGuid()}")));
        // an optional site: narrows the person list and the report to that site's work orders; All sites does not
        var siteA = (await lead.GetElAsync($"{U}/{id}")).GetProperty("record").GetProperty("siteId").GetGuid();
        var atA = People(await lead.GetElAsync($"{C}/people?kind=Day&from=2001-02-14&siteId={siteA}"));
        Assert.Contains(tech, atA); Assert.Contains(worker, atA); Assert.DoesNotContain(other, atA);
        var siteRep = await (await lead.PostOkAsync(C, new { sections, period = new { kind = "Week", from = "2001-02-14", siteId = siteA } })).JsonAsync();
        var vs = (await lead.GetElAsync($"{C}/{siteRep.Id()}/view")).EnumerateArray().ToList();
        Assert.StartsWith("8.50", vs.Single(b => b.GetProperty("key").GetString() == "sumHours").GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.DoesNotContain(vs.Single(b => b.GetProperty("key").GetString() == "sumByPerson").GetProperty("rows").EnumerateArray(), r => r[0].GetString() == other);
        Assert.Equal(siteA, siteRep.GetProperty("period").GetProperty("siteId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { period = new { kind = "Day", from = "2001-02-14", siteId = Guid.NewGuid() } })).StatusCode);
        Assert.Equal(list.Order(StringComparer.OrdinalIgnoreCase).ToList(), list);
        Assert.Equal(list.Count, list.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var dw = await (await lead.PostOkAsync(C, new { sections, period = new { kind = "Day", from = "2001-02-14", person = worker } })).JsonAsync();
        var v3 = (await lead.GetElAsync($"{C}/{dw.Id()}/view")).EnumerateArray().ToList();
        Assert.StartsWith("1.50", v3.Single(b => b.GetProperty("key").GetString() == "sumHours").GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal([worker], v3.Single(b => b.GetProperty("key").GetString() == "sumByPerson").GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).Where(n => n != "Total").ToList());
        // pauses with reasons in today's period
        var now = await (await lead.PostOkAsync(C, new { sections = new[] { "sumPauses", "sumCounts" }, period = new { kind = "Custom", from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd") } })).JsonAsync();
        var v2 = (await lead.GetElAsync($"{C}/{now.Id()}/view")).EnumerateArray().ToList();
        Assert.Contains(v2.Single(b => b.GetProperty("key").GetString() == "sumPauses").GetProperty("rows").EnumerateArray(), r => r[1].GetString() == "Waiting for made-up parts");
        // bad periods
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { period = new { kind = "Custom", from = "2001-02-14", to = "2001-02-01" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { period = new { kind = "Fortnight", from = "2001-02-14" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync(C, new { period = new { kind = "Day" } })).StatusCode);
        var pdf = await lead.GetAsync($"{C}/{rep.Id()}/pdf");
        Assert.True(pdf.IsSuccessStatusCode, await pdf.Content.ReadAsStringAsync());
        var xlsx = await lead.GetAsync($"{C}/{rep.Id()}/xlsx");
        Assert.True(xlsx.IsSuccessStatusCode);
        var all = string.Join(" ", view.Select(b => b.GetRawText())).ToLowerInvariant();
        Assert.DoesNotContain("pay", all.Replace("paused", "")); Assert.DoesNotContain("audit", all); Assert.DoesNotContain("compliance", all);
    }
}
