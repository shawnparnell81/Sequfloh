using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Tests;

/// <summary>The shop day clock: arrival, one task at a time, breaks, the 4:00 PM close, and audited corrections. Made-up people only.</summary>
[Collection("api")]
public sealed class DayClockTests(TestApp app)
{
    const string D = "/api/sequfloh/day-clock";
    static readonly string[] Own = [Permissions.SequflohAccess, Permissions.SequflohViewOwnDayClock];
    static readonly string[] Worker = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohViewOwnDayClock];

    [Fact]
    public async Task Clock_in_and_out_record_the_arrival_and_a_manual_departure()
    {
        var (_, c) = await app.CreateUserAsync(Own);
        var inn = await c.PostOkAsync($"{D}/in");
        var status = await inn.JsonAsync();
        Assert.True(status.GetProperty("clockedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("activity").ValueKind);
        var sheet = await c.GetElAsync($"{D}/timesheet");
        Assert.NotEqual(JsonValueKind.Null, sheet.GetProperty("days")[0].GetProperty("arrival").ValueKind);
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("days")[0].GetProperty("clockOut").ValueKind);

        await c.PostOkAsync($"{D}/out");
        var after = await c.GetElAsync(D);
        Assert.False(after.GetProperty("clockedIn").GetBoolean());
        var day = (await c.GetElAsync($"{D}/timesheet")).GetProperty("days")[0];
        Assert.Equal("Manual", day.GetProperty("clockOutKind").GetString());
        Assert.False(day.GetProperty("automatic").GetBoolean());
        Assert.Contains((await c.GetElAsync($"{D}/history")).GetProperty("entries").EnumerateArray(), e => (e.GetProperty("description").GetString() ?? "").Contains("clocked out"));
    }

    [Fact]
    public async Task A_job_or_indirect_task_needs_a_day_clock_unless_clock_in_and_start_is_asked_for()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Worker, site);
        var id = await WorkOrderAsync(c, site);
        var blocked = await c.PostAsync($"/api/sequfloh/tracking/{id}/time/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains(SequflohTimeService.ClockInFirstMessage, await blocked.Content.ReadAsStringAsync());

        var task = await TaskIdAsync(c, "Lab Setup");
        var indirect = await c.PostAsync($"{D}/indirect", new { taskId = task, note = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, indirect.StatusCode);

        await c.PostOkAsync($"/api/sequfloh/tracking/{id}/time/start", new { clockIn = true });
        var on = await c.GetElAsync(D);
        Assert.True(on.GetProperty("clockedIn").GetBoolean());
        Assert.Equal("WorkOrder", on.GetProperty("activity").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Switching_indirect_tasks_ends_the_previous_one_and_a_break_can_be_resumed()
    {
        var (_, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        var lab = await TaskIdAsync(c, "Lab Setup");
        var training = await TaskIdAsync(c, "Training");
        await c.PostOkAsync($"{D}/indirect", new { taskId = lab, note = "Benches" });
        await c.PostOkAsync($"{D}/indirect", new { taskId = training, note = (string?)null });
        var mid = await c.GetElAsync(D);
        Assert.Equal("Training", mid.GetProperty("activity").GetProperty("name").GetString());

        await c.PostOkAsync($"{D}/pause", new { reason = "Break" });
        var paused = await c.GetElAsync(D);
        Assert.Equal("Break", paused.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Equal("Training", paused.GetProperty("activity").GetProperty("resumeName").GetString());

        await c.PostOkAsync($"{D}/resume");
        var back = await c.GetElAsync(D);
        Assert.Equal("Indirect", back.GetProperty("activity").GetProperty("kind").GetString());
        Assert.Equal("Training", back.GetProperty("activity").GetProperty("name").GetString());

        await c.PostOkAsync($"{D}/out");
        var day = (await c.GetElAsync($"{D}/timesheet")).GetProperty("days")[0];
        var names = day.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("Lab Setup", names);
        Assert.Contains("Training", names);
        Assert.Contains("Break", names);
        Assert.Contains(day.GetProperty("indirect").EnumerateArray(), t => t.GetProperty("task").GetString() == "Training");
        Assert.True(day.GetProperty("breakSeconds").GetInt64() >= 0);
    }

    [Fact]
    public async Task A_break_pauses_the_running_job_and_resume_continues_it()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Worker, site);
        var id = await WorkOrderAsync(c, site);
        await c.PostOkAsync($"{D}/in");
        await c.PostOkAsync($"/api/sequfloh/tracking/{id}/time/start");
        await c.PostOkAsync($"{D}/pause", new { reason = "Lunch" });
        var job = await c.GetElAsync($"/api/sequfloh/tracking/{id}/time");
        Assert.True(job.GetProperty("minePaused").GetBoolean());
        Assert.Equal("Lunch", job.GetProperty("minePauseReason").GetString());
        var day = await c.GetElAsync(D);
        Assert.Equal("Lunch", day.GetProperty("activity").GetProperty("kind").GetString());

        await c.PostOkAsync($"{D}/resume");
        var again = await c.GetElAsync(D);
        Assert.Equal("WorkOrder", again.GetProperty("activity").GetProperty("kind").GetString());
        Assert.False((await c.GetElAsync($"/api/sequfloh/tracking/{id}/time")).GetProperty("minePaused").GetBoolean());
    }

    [Fact]
    public async Task Only_one_activity_runs_and_a_deactivated_task_cannot_be_started()
    {
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        var meeting = await TaskIdAsync(c, "Meetings");
        await c.PostOkAsync($"{D}/indirect", new { taskId = meeting });
        var values = await admin.GetElAsync("/api/sequfloh/reference/indirect-tasks");
        var row = values.EnumerateArray().First(v => v.GetProperty("label").GetString() == "Meetings");
        var meetingId = row.GetProperty("id").GetGuid();
        try
        {
            await admin.PostOkAsync($"/api/sequfloh/reference/values/{meetingId}/deactivate");
            var again = await c.PostAsync($"{D}/indirect", new { taskId = meeting });
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
            var offered = (await c.GetElAsync(D)).GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("label").GetString());
            Assert.DoesNotContain("Meetings", offered);
        }
        finally { await admin.PostOkAsync($"/api/sequfloh/reference/values/{meetingId}/activate"); }
    }

    [Fact]
    public async Task Sweep_closes_at_4_pm_Eastern_even_after_a_new_scope_and_a_second_pass()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var winterEnd = DayClockTime.AtLocal(new DateTime(2026, 1, 15, 16, 0, 0), zone);
        var summerEnd = DayClockTime.AtLocal(new DateTime(2026, 7, 15, 16, 0, 0), zone);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 16, 0, 0, TimeSpan.FromHours(-5)), winterEnd);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.FromHours(-4)), summerEnd);

        var site = await app.CreateSiteAsync();
        var (uid, c) = await app.CreateUserAsync(Worker, site);
        var id = await WorkOrderAsync(c, site);
        await c.PostOkAsync($"{D}/in");
        await c.PostOkAsync($"/api/sequfloh/tracking/{id}/time/start");
        await BackdateAsync(uid, new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero)); // 8:00 AM ET

        var first = await SweepAsync(winterEnd.AddMinutes(10));
        Assert.True(first >= 1);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = await db.SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid);
            Assert.Equal(winterEnd, clock.EndedAt);
            Assert.Equal("Automatic", clock.EndKind);
            var entry = await db.SequflohTimeEntries.AsNoTracking().SingleAsync(e => e.UserId == uid && e.TrackingRecordId == id);
            Assert.Equal(winterEnd, entry.StoppedAt);
            Assert.InRange(entry.Seconds, 8 * 3600 - 5, 8 * 3600 + 5);
        }
        Assert.Equal(0, await SweepAsync(winterEnd.AddMinutes(30)));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = await db.SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid);
            Assert.Equal(winterEnd, clock.EndedAt);
        }

        var (uid2, c2) = await app.CreateUserAsync(Own);
        await c2.PostOkAsync($"{D}/in");
        await BackdateAsync(uid2, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)); // 8:00 AM EDT
        Assert.True(await SweepAsync(summerEnd.AddMinutes(5)) >= 1);
        await using var check = app.Services.CreateAsyncScope();
        var ended = await check.ServiceProvider.GetRequiredService<AppDbContext>().SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid2);
        Assert.Equal(summerEnd, ended.EndedAt);
    }

    [Fact]
    public async Task Working_late_stays_open_past_the_end_and_the_hard_cap_closes_it()
    {
        var (_, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        await c.PostOkAsync($"{D}/late");
        var uid = (await c.GetElAsync("/api/sequfloh/context")).GetProperty("user").GetProperty("id").GetGuid();
        await BackdateAsync(uid, new DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero));
        var four = DayClockTime.AtLocal(new DateTime(2026, 1, 15, 16, 0, 0));
        var five = four.AddHours(1);
        var cap = four.AddHours(4);
        Assert.Equal(0, await SweepAsync(five));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var clock = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid);
            Assert.Null(clock.EndedAt);
            Assert.True(clock.WorkingLate);
        }
        Assert.True(await SweepAsync(cap.AddMinutes(1)) >= 1);
        await using var end = app.Services.CreateAsyncScope();
        var closed = await end.ServiceProvider.GetRequiredService<AppDbContext>().SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid);
        Assert.Equal(cap, closed.EndedAt);
        Assert.Equal("HardCap", closed.EndKind);
    }

    [Fact]
    public async Task Someone_who_clocks_in_after_the_end_is_not_closed_until_the_next_day()
    {
        var (_, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        var uid = (await c.GetElAsync("/api/sequfloh/context")).GetProperty("user").GetProperty("id").GetGuid();
        var evening = DayClockTime.AtLocal(new DateTime(2026, 1, 15, 17, 30, 0));
        await BackdateAsync(uid, evening);
        Assert.Equal(0, await SweepAsync(evening.AddHours(2)));
        await using var scope = app.Services.CreateAsyncScope();
        var clock = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SequflohDayClocks.AsNoTracking().SingleAsync(x => x.UserId == uid);
        Assert.Null(clock.EndedAt);
    }

    [Fact]
    public async Task Unaccounted_time_is_the_part_of_the_day_spent_on_nothing()
    {
        var (_, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        var lab = await TaskIdAsync(c, "Lab Cleaning");
        await c.PostOkAsync($"{D}/indirect", new { taskId = lab });
        await c.PostOkAsync($"{D}/out");
        var uid = (await c.GetElAsync("/api/sequfloh/context")).GetProperty("user").GetProperty("id").GetGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = await db.SequflohDayClocks.SingleAsync(x => x.UserId == uid);
            clock.StartedAt = clock.StartedAt.AddHours(-2);
            var seg = await db.SequflohDaySegments.SingleAsync(s => s.UserId == uid);
            seg.StartedAt = clock.EndedAt!.Value.AddMinutes(-30);
            seg.Seconds = (long)(seg.EndedAt!.Value - seg.StartedAt).TotalSeconds;
            await db.SaveChangesAsync();
        }
        var day = (await c.GetElAsync($"{D}/timesheet")).GetProperty("days")[0];
        Assert.InRange(day.GetProperty("indirectSeconds").GetInt64(), 29 * 60, 31 * 60);
        Assert.InRange(day.GetProperty("unaccountedSeconds").GetInt64(), 89 * 60, 91 * 60);
    }

    [Fact]
    public async Task Visibility_follows_the_day_clock_permissions_and_not_a_role_name()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var dept = await (await admin.PostOkAsync($"/api/organization/sites/{site}/departments", new { code = "LAB" + Guid.NewGuid().ToString("N")[..4], name = "Lab" })).Content.ReadFromJsonAsync<Guid>();
        var other = await (await admin.PostOkAsync($"/api/organization/sites/{site}/departments", new { code = "OFF" + Guid.NewGuid().ToString("N")[..4], name = "Office" })).Content.ReadFromJsonAsync<Guid>();
        var (a, own) = await app.CreateUserAsync(Own, site, dept);
        var (b, team) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewTeamDayClock], site, dept);
        var (outsider, outside) = await app.CreateUserAsync(Own, site, other);
        var (_, everyone) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewAllDayClock]);
        await own.PostOkAsync($"{D}/in");
        await team.PostOkAsync($"{D}/in");
        await outside.PostOkAsync($"{D}/in");

        Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync($"{D}/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await own.GetAsync($"{D}/timesheet?userId={b}")).StatusCode);
        var seen = (await team.GetElAsync($"{D}/team")).GetProperty("people").EnumerateArray().Select(p => p.GetProperty("userId").GetGuid()).ToList();
        Assert.Contains(a, seen);
        Assert.Contains(b, seen);
        Assert.DoesNotContain(outsider, seen);
        var all = (await everyone.GetElAsync($"{D}/team")).GetProperty("people").EnumerateArray().Select(p => p.GetProperty("userId").GetGuid()).ToList();
        Assert.Contains(outsider, all);

        var (_, settings) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        Assert.Equal(HttpStatusCode.Forbidden, (await settings.PutAsync("/api/sequfloh/settings/dayClock", new { value = new { endTime = "15:30", hardCapMinutes = 60, warnMinutes = 10 }, rowVersion = 0 })).StatusCode);
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageDayClock]);
        var row = (await mgr.GetElAsync("/api/sequfloh/settings")).GetProperty("rowVersion").GetUInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.PutAsync("/api/sequfloh/settings/dayClock", new { value = new { endTime = "16:00", hardCapMinutes = 240, warnMinutes = 15 }, rowVersion = row })).StatusCode);
    }

    [Fact]
    public async Task A_correction_needs_the_permission_and_a_reason_and_is_audited_for_the_person()
    {
        var (uid, c) = await app.CreateUserAsync(Own);
        await c.PostOkAsync($"{D}/in");
        await c.PostOkAsync($"{D}/out");
        var denied = await c.PostAsync($"{D}/corrections", new { reason = "Forgot the real time", userId = uid, action = "add-clock", startedAt = DateTimeOffset.UtcNow.AddHours(-5), endedAt = DateTimeOffset.UtcNow.AddHours(-4) });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var (_, fixer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCorrectDayClock, Permissions.SequflohViewAllDayClock, Permissions.SequflohExportRecords]);
        var missing = await fixer.PostAsync($"{D}/corrections", new { reason = " ", userId = uid, action = "add-clock", startedAt = DateTimeOffset.UtcNow.AddDays(-2).AddHours(-6), endedAt = DateTimeOffset.UtcNow.AddDays(-2).AddHours(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var start = DateTimeOffset.UtcNow.AddDays(-3).AddHours(-8);
        await fixer.PostOkAsync($"{D}/corrections", new { reason = "Missed punch from the bench", userId = uid, action = "add-clock", startedAt = start, endedAt = start.AddHours(7) });
        var history = await c.GetElAsync($"{D}/history");
        Assert.Contains(history.GetProperty("entries").EnumerateArray(), e => (e.GetProperty("description").GetString() ?? "").Contains("Missed punch from the bench"));
        var csv = await fixer.GetAsync($"{D}/export.csv?userId={uid}&from={DateOnly.FromDateTime(start.UtcDateTime.AddDays(-1)):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Arrival", text);
        Assert.Contains("Unaccounted", text);
    }

    [Fact]
    public async Task Indirect_tasks_are_a_reference_list_an_administrator_can_edit()
    {
        var admin = await app.AdminAsync();
        var lists = await admin.GetElAsync("/api/sequfloh/reference");
        Assert.Contains(lists.EnumerateArray(), l => l.GetProperty("key").GetString() == "indirect-tasks" && l.GetProperty("label").GetString() == "Indirect tasks");
        var created = await (await admin.PostOkAsync("/api/sequfloh/reference/indirect-tasks", new { code = "", label = "Kaizen board", description = "Made-up task", isActive = true })).JsonAsync();
        var id = created.GetProperty("id").GetGuid();
        await admin.PutOkAsync($"/api/sequfloh/reference/values/{id}", new { code = "KAIZEN_BOARD", label = "Kaizen", description = "Renamed", isActive = true, rowVersion = 0 });
        var values = (await admin.GetElAsync("/api/sequfloh/reference/indirect-tasks")).EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).ToList();
        values.Remove(id);
        values.Insert(0, id);
        await admin.PostOkAsync("/api/sequfloh/reference/indirect-tasks/reorder", new { ids = values });
        var first = (await admin.GetElAsync("/api/sequfloh/reference/indirect-tasks")).EnumerateArray().First();
        Assert.Equal("Kaizen", first.GetProperty("label").GetString());
        await admin.PostOkAsync($"/api/sequfloh/reference/values/{id}/deactivate");
    }

    static async Task<Guid> WorkOrderAsync(ApiClient c, Guid site)
    {
        var id = (await (await c.PostOkAsync("/api/sequfloh/tracking", new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await c.StartAsync(id);
        return id;
    }

    static async Task<Guid> TaskIdAsync(ApiClient c, string label)
    {
        var tasks = (await c.GetElAsync(D)).GetProperty("tasks");
        return tasks.EnumerateArray().First(t => t.GetProperty("label").GetString() == label).GetProperty("id").GetGuid();
    }

    async Task BackdateAsync(Guid userId, DateTimeOffset started)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = await db.SequflohDayClocks.SingleAsync(x => x.UserId == userId && x.EndedAt == null);
        clock.StartedAt = started;
        var segments = await db.SequflohDaySegments.Where(s => s.UserId == userId && s.EndedAt == null).ToListAsync();
        foreach (var s in segments) s.StartedAt = started;
        var entries = await db.SequflohTimeEntries.Where(e => e.UserId == userId && e.StoppedAt == null).ToListAsync();
        foreach (var e in entries) { e.StartedAt = started; e.Events = "[]"; }
        await db.SaveChangesAsync();
    }

    async Task<int> SweepAsync(DateTimeOffset now)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SequflohDayClockService>().SweepAsync(now, CancellationToken.None);
    }
}
