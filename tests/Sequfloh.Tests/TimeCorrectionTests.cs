using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Moving recorded time to another work order and removing it with a reason (voided, kept in history). Made-up data only.</summary>
[Collection("api")]
public sealed class TimeCorrectionTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Base = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime, Permissions.SequflohViewReports,
        Permissions.SequflohDispatchTask, Permissions.SequflohManageChargebacks];
    static readonly string[] Fixer = [.. Base, Permissions.SequflohTransferTime, Permissions.SequflohRemoveTime];

    static async Task<Guid> NewAsync(ApiClient c, Guid site)
    {
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await c.StartAsync(id);
        return id;
    }
    static async Task<string> No(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number")!;
    static async Task<Guid> WorkerAsync(ApiClient c, Guid id, string name) =>
        (await (await c.PostOkAsync($"{T}/{id}/crew/workers", new { name })).JsonAsync()).GetProperty("workers").EnumerateArray().First(w => w.Str("name") == name).Id();

    [Fact]
    public async Task Time_moves_intact_to_another_work_order_and_removed_time_stops_counting_but_stays_in_history()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Fixer, site);
        var (_, plain) = await app.CreateUserAsync(Base, site);
        var a = await NewAsync(c, site); var b = await NewAsync(c, site);
        var aNo = await No(c, a); var bNo = await No(c, b);
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1).Date.AddHours(13), TimeSpan.Zero);
        var w = await WorkerAsync(c, a, "Made-up Adi");
        await c.PostOkAsync($"{T}/{a}/crew/sessions", new { crewSize = 1, workerId = w, start, stop = start.AddHours(3), pauses = new[] { new { from = start.AddHours(1), to = start.AddHours(1.5) } }, note = "made-up note" });
        await c.PostOkAsync($"{T}/{a}/crew/sessions", new { crewSize = 2, start, stop = start.AddHours(2) });
        await c.PostOkAsync($"{T}/{a}/time/start"); await c.PostOkAsync($"{T}/{a}/time/stop");
        var cb = (await (await c.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = "WO-MADEUP-TC", trackingRecordId = a })).JsonAsync()).Id();

        var list = (await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        var crewIds = list.Where(e => e.Str("kind") != "user").Select(e => e.Id()).ToArray();
        var userIds = list.Where(e => e.Str("kind") == "user").Select(e => e.Id()).ToArray();
        Assert.False((await plain.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("canTransfer").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{T}/{a}/time-corrections/transfer", new { targetId = b, crewSessionIds = crewIds })).StatusCode);

        // a same-name day worker already on B at an overlapping time (older data saved before the one-job rule) blocks the move, nothing moves
        var wb = await WorkerAsync(c, b, "made-up adi");
        using (var s0 = app.Services.CreateScope())
        {
            var d0 = s0.ServiceProvider.GetRequiredService<AppDbContext>();
            d0.SequflohCrewSessions.Add(new Sequfloh.Domain.Execution.SequflohCrewSession { TrackingRecordId = b, TrackingNumber = bNo, CrewSize = 1, WorkerId = wb, WorkerName = "made-up adi", StartedAt = start.AddHours(2), StoppedAt = start.AddHours(4) });
            await d0.SaveChangesAsync();
        }
        var bad = await c.PostAsync($"{T}/{a}/time-corrections/transfer", new { targetId = b, crewSessionIds = crewIds, timeEntryIds = userIds });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        Assert.Contains("Nothing was moved", await bad.Content.ReadAsStringAsync());
        Assert.Equal(3, (await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("entries").GetArrayLength());

        var c2 = await NewAsync(c, site); var cNo = await No(c, c2);
        await c.PostOkAsync($"{T}/{a}/time-corrections/transfer", new { targetId = c2, crewSessionIds = crewIds, timeEntryIds = userIds, reason = "Booked on the wrong order" });
        Assert.Equal(0, (await c.GetElAsync($"{T}/{a}/crew")).GetProperty("totalSeconds").GetInt64());
        var moved = await c.GetElAsync($"{T}/{c2}/crew");
        Assert.InRange(moved.GetProperty("totalSeconds").GetInt64(), (2.5 + 4) * 3600, (2.5 + 4) * 3600 + 60);
        var adi = moved.GetProperty("sessions").EnumerateArray().First(s => s.Str("workerName") == "Made-up Adi");
        Assert.Equal("made-up note", adi.Str("note")); Assert.Equal(1800, adi.GetProperty("pausedSeconds").GetInt64());
        Assert.Contains(moved.GetProperty("workers").EnumerateArray(), x => x.Str("name") == "Made-up Adi");
        var rep = await c.GetElAsync($"{T}/{c2}/job/report");
        Assert.Contains(rep.GetProperty("people").EnumerateArray(), p => p.Str("name") == "Made-up Adi (day worker)");
        var cbEl = await c.GetElAsync($"/api/sequfloh/chargebacks/{cb}");
        Assert.Equal(JsonValueKind.String, cbEl.GetProperty("timeChangedAt").ValueKind);
        Assert.Contains($"moved from {aNo} to {cNo}", cbEl.Str("timeChangedNote"));

        // remove needs a reason; removed time stops counting but stays listed
        var row = moved.GetProperty("sessions").EnumerateArray().First(s => s.GetProperty("crewSize").GetInt32() == 2).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{c2}/time-corrections/remove", new { crewSessionIds = new[] { row }, reason = " " })).StatusCode);
        await c.PostOkAsync($"{T}/{c2}/time-corrections/remove", new { crewSessionIds = new[] { row }, reason = "Made-up duplicate" });
        Assert.InRange((await c.GetElAsync($"{T}/{c2}/crew")).GetProperty("totalSeconds").GetInt64(), 2.5 * 3600, 2.5 * 3600 + 60);
        Assert.DoesNotContain((await c.GetElAsync($"{T}/{c2}/job/report")).GetProperty("people").EnumerateArray(), p => p.Str("name") == "Crew (unnamed workers)");
        var voided = (await c.GetElAsync($"{T}/{c2}/time-corrections")).GetProperty("entries").EnumerateArray().First(e => e.Id() == row);
        Assert.Equal("Made-up duplicate", voided.Str("voidReason"));

        // a clocked-on entry can't be moved
        await c.PostOkAsync($"{T}/{c2}/crew/start", new { crewSize = 1 });
        var run = (await c.GetElAsync($"{T}/{c2}/crew")).GetProperty("running").Id();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{c2}/time-corrections/transfer", new { targetId = a, crewSessionIds = new[] { run } })).StatusCode);
        await c.PostOkAsync($"{T}/{c2}/crew/stop", new { });

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditA = await db.AuditEntries.Where(x => x.EntityId == a).Select(x => x.Action + ": " + x.Description).ToListAsync();
        var auditC = await db.AuditEntries.Where(x => x.EntityId == c2).Select(x => x.Action + ": " + x.Description).ToListAsync();
        Assert.Contains(auditA, x => x.StartsWith("TimeTransferredOut") && x.Contains($"from {aNo} to {cNo}") && x.Contains("Reason: Booked on the wrong order"));
        Assert.Contains(auditC, x => x.StartsWith("TimeTransferredIn") && x.Contains("day worker Made-up Adi"));
        Assert.Contains(auditC, x => x.StartsWith("TimeRemoved") && x.Contains("Reason: Made-up duplicate"));
        Assert.True(await db.SequflohCrewSessions.IgnoreQueryFilters().AnyAsync(s => s.Id == row)); // nothing is hard-deleted
        _ = bNo;
    }
}
