using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>The per-work-order Use crew time setting: what counts, what is refused, and the default for older work orders. Made-up data only.</summary>
[Collection("api")]
public sealed class CrewTimeSettingTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Lead = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime,
        Permissions.SequflohManageChargebacks, Permissions.SequflohViewReports, Permissions.SequflohDispatchTask];
    static readonly string[] Plain = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohDispatchTask];

    async Task<(Guid Id, string No, ApiClient Lead, ApiClient Plain)> NewAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, lead) = await app.CreateUserAsync(Lead, site);
        var (_, plain) = await app.CreateUserAsync(Plain, site);
        var id = (await (await lead.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await lead.StartAsync(id);
        return (id, (await lead.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number")!, lead, plain);
    }

    async Task AddCrewRowAsync(Guid id, string no, int crew, DateTimeOffset from, double hours, string? note = null)
    {
        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = id, TrackingNumber = no, CrewSize = crew, StartedAt = from, StoppedAt = from.AddHours(hours), Entered = true, Note = note });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Crew_rows_count_toward_personnel_hours_and_chargeback_only_when_crew_time_is_on()
    {
        var (id, no, lead, _) = await NewAsync();
        Assert.False((await lead.GetElAsync($"{T}/{id}/crew")).GetProperty("useCrewTime").GetBoolean()); // new work orders start with No
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        await AddCrewRowAsync(id, no, 3, start, 2); // an older crew row

        var off = await lead.GetElAsync($"{T}/{id}/crew");
        Assert.Equal(0, off.GetProperty("personnel").GetInt32());
        Assert.Equal(0, off.GetProperty("totalSeconds").GetInt64());
        var row = Assert.Single(off.GetProperty("sessions").EnumerateArray());
        Assert.False(row.GetProperty("counted").GetBoolean()); // still listed, marked not counted
        var pre = await lead.GetElAsync($"{C}/prefill/{id}");
        Assert.Equal(0, pre.Int("totalPersonnel"));
        Assert.Equal(0m, pre.GetProperty("totalHours").GetDecimal());
        Assert.DoesNotContain((await lead.GetElAsync($"{T}/{id}/job/report")).GetProperty("people").EnumerateArray(), p => p.Str("name") == "Crew (unnamed workers)");

        await lead.UseCrewTimeAsync(id);
        var on = await lead.GetElAsync($"{T}/{id}/crew");
        Assert.True(on.GetProperty("useCrewTime").GetBoolean());
        Assert.Equal(3, on.GetProperty("personnel").GetInt32());
        Assert.Equal(6 * 3600, on.GetProperty("totalSeconds").GetInt64());
        Assert.True(on.GetProperty("sessions")[0].GetProperty("counted").GetBoolean());
        pre = await lead.GetElAsync($"{C}/prefill/{id}");
        Assert.Equal(3, pre.Int("totalPersonnel"));
        Assert.Equal(6m, Math.Round(pre.GetProperty("totalHours").GetDecimal()));
        Assert.Contains((await lead.GetElAsync($"{T}/{id}/job/report")).GetProperty("people").EnumerateArray(), p => p.Str("name") == "Crew (unnamed workers)");

        // a saved chargeback keeps its values when the setting changes
        var cb = await (await lead.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-CT1", trackingRecordId = id, totalPersonnel = 3, hourlyRate = 10 })).JsonAsync();
        var before = cb.GetProperty("totalHours").GetDecimal();
        await lead.UseCrewTimeAsync(id, false);
        var after = await lead.GetElAsync($"{C}/{cb.Id()}");
        Assert.Equal(before, after.GetProperty("totalHours").GetDecimal());
        Assert.Equal(3, after.Int("totalPersonnel"));
    }

    [Fact]
    public async Task Crew_time_and_named_time_can_not_be_mixed_and_the_server_refuses_each_way()
    {
        var (id, no, lead, plain) = await NewAsync();
        var start = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        // crew off: nameless crew entries and bulk entry are refused
        var r = await lead.PostAsync($"{T}/{id}/crew/sessions", new { crewSize = 2, start, stop = start.AddHours(1) });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains(SequflohCrewService.CrewOffMessage, await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{T}/{id}/crew/start", new { crewSize = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PostAsync($"{T}/{id}/crew/bulk?preview=true", new { from = "2026-10-05", to = "2026-10-06", crewSize = 2, hoursPerDay = 8 })).StatusCode);

        // only the permission changes the setting
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsync($"{T}/{id}/crew/setting", new { useCrewTime = true })).StatusCode);

        // named time exists: turning crew time on is refused
        await plain.PostOkAsync($"{T}/{id}/time/start");
        await plain.PostOkAsync($"{T}/{id}/time/stop");
        var on = await lead.PutAsync($"{T}/{id}/crew/setting", new { useCrewTime = true });
        Assert.Equal(HttpStatusCode.Conflict, on.StatusCode);
        Assert.Contains("can't be mixed", await on.Content.ReadAsStringAsync());

        // day-worker time also blocks it
        var (id2, _, lead2, plain2) = await NewAsync();
        var w = (await (await lead2.PostOkAsync($"{T}/{id2}/crew/workers", new { name = "Made-up Robin" })).JsonAsync()).GetProperty("workers")[0].Id();
        await lead2.PostOkAsync($"{T}/{id2}/crew/sessions", new { crewSize = 1, workerId = w, start, stop = start.AddHours(2) });
        Assert.Equal(HttpStatusCode.Conflict, (await lead2.PutAsync($"{T}/{id2}/crew/setting", new { useCrewTime = true })).StatusCode);

        // crew on: named and day-worker clock-on and entries are refused
        var (id3, _, lead3, plain3) = await NewAsync();
        await lead3.UseCrewTimeAsync(id3);
        var named = await plain3.PostAsync($"{T}/{id3}/time/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, named.StatusCode);
        Assert.Contains(SequflohCrewService.NamedBlockedMessage, await named.Content.ReadAsStringAsync());
        var w3 = (await (await lead3.PostOkAsync($"{T}/{id3}/crew/workers", new { name = "Made-up Sam" })).JsonAsync()).GetProperty("workers")[0].Id();
        Assert.Equal(HttpStatusCode.Conflict, (await lead3.PostAsync($"{T}/{id3}/crew/start", new { crewSize = 1, workerId = w3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lead3.PostAsync($"{T}/{id3}/crew/sessions", new { crewSize = 1, workerId = w3, start, stop = start.AddHours(1) })).StatusCode);
        // the crew itself works, and can't be switched off while clocked on
        await lead3.PostOkAsync($"{T}/{id3}/crew/start", new { crewSize = 2 });
        Assert.Equal(HttpStatusCode.Conflict, (await lead3.PutAsync($"{T}/{id3}/crew/setting", new { useCrewTime = false })).StatusCode);
        await lead3.PostOkAsync($"{T}/{id3}/crew/stop", new { });
        await lead3.UseCrewTimeAsync(id3, false);

        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == id3 && a.Action == "CrewTimeSetting").Select(a => a.Description).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Contains("set to Yes")); Assert.Contains(audit, a => a.Contains("set to No"));
    }

    [Fact]
    public async Task Older_work_orders_get_crew_time_only_for_hand_entered_crew_rows_with_no_named_time()
    {
        var auto = await NewAsync();
        var manual = await NewAsync();
        var mixed = await NewAsync();
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        await AddCrewRowAsync(auto.Id, auto.No, 1, start, 0.2, "Creating the work order");
        await AddCrewRowAsync(manual.Id, manual.No, 2, start, 3);
        await AddCrewRowAsync(mixed.Id, mixed.No, 2, start, 3);
        await mixed.Plain.PostOkAsync($"{T}/{mixed.Id}/time/start");
        await mixed.Plain.PostOkAsync($"{T}/{mixed.Id}/time/stop");

        using var s = app.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(Sequfloh.Data.Migrations.CrewTimeSetting.DefaultSql);
        var ids = new[] { auto.Id, manual.Id, mixed.Id };
        var got = await db.SequflohTrackingRecords.IgnoreQueryFilters().AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.UseCrewTime);
        await tx.RollbackAsync();
        Assert.False(got[auto.Id]);   // the automatic Creating the work order entry: No, so it is not counted
        Assert.True(got[manual.Id]);  // crew time typed in by hand and nothing else: Yes
        Assert.False(got[mixed.Id]);  // hand-entered crew next to named time: No
    }
}
