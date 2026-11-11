using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Tests;

/// <summary>Correcting a forgotten clock-off or pause, and the start or end of a finished entry. Made-up data only.</summary>
[Collection("api")]
public sealed class AdjustTimeTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Base = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks];
    static readonly string[] Fixer = [.. Base, Permissions.SequflohAdjustTime];

    static async Task<Guid> NewAsync(ApiClient c, Guid site)
    {
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 60 })).JsonAsync()).Id();
        await c.StartAsync(id);
        return id;
    }

    async Task BackdateAsync(Guid entryId, DateTimeOffset start)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var e = await db.SequflohTimeEntries.SingleAsync(x => x.Id == entryId);
        e.StartedAt = start;
        e.Events = JsonSerializer.Serialize(new[] { new SequflohTimeService.TimeEvent("Start", start) }, SequflohJson.Options);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Administrator_has_adjust_time_and_other_roles_do_not_until_it_is_ticked()
    {
        var admin = await app.AdminAsync();
        var grants = (await admin.GetElAsync("/api/auth/session")).GetProperty("user").GetProperty("grants").EnumerateArray().Select(g => g.Str("permission")).ToList();
        Assert.Contains(Permissions.SequflohAdjustTime, grants);
        var roles = (await admin.GetElAsync("/api/admin/roles")).EnumerateArray().ToList();
        var op = roles.Single(r => r.Str("name") == "Operator").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.DoesNotContain(Permissions.SequflohAdjustTime, op);
        var sup = roles.Single(r => r.Str("name") == "Supervisor").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.DoesNotContain(Permissions.SequflohAdjustTime, sup);
    }

    [Fact]
    public async Task A_forgotten_clock_off_can_be_corrected_with_a_reason_and_hours_are_worked_out_again()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Fixer, site);
        var (_, plain) = await app.CreateUserAsync(Base, site);
        var (_, worker) = await app.CreateUserAsync(Base, site);
        var fixerName = (await c.GetElAsync("/api/auth/session")).GetProperty("user").Str("displayName");
        var workerName = (await worker.GetElAsync("/api/auth/session")).GetProperty("user").Str("displayName");
        var a = await NewAsync(c, site);
        var number = (await c.GetElAsync($"{T}/{a}")).GetProperty("record").Str("number");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a).ExecuteUpdateAsync(u => u.SetProperty(r => r.TrackTimeVariance, true));
        }

        await worker.PostOkAsync($"{T}/{a}/time/start", new { clockIn = true });
        var openId = (await c.GetElAsync($"{T}/{a}/time")).GetProperty("entries").EnumerateArray().Single().Id();
        var start = DateTimeOffset.UtcNow.AddHours(-20);
        await BackdateAsync(openId, start);

        var before = await c.GetElAsync($"{T}/{a}/time");
        Assert.Contains("over 12 hours", before.GetProperty("people").EnumerateArray().Single().Str("staleNote"));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("entries").EnumerateArray().Single().GetProperty("stoppedAt").ValueKind);
        Assert.False((await plain.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("canAdjust").GetBoolean());
        Assert.True((await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("canAdjust").GetBoolean());

        var stop = start.AddHours(2);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = stop, reason = "forgot to clock off" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = stop, reason = " " })).StatusCode);
        var future = await c.PostAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = DateTimeOffset.UtcNow.AddHours(2), reason = "forgot to clock off" });
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("future", await future.Content.ReadAsStringAsync());
        var backwards = await c.PostAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = start.AddMinutes(-10), reason = "forgot to clock off" });
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Contains("after the start", await backwards.Content.ReadAsStringAsync());

        Guid clashId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var open = await db.SequflohTimeEntries.AsNoTracking().SingleAsync(x => x.Id == openId);
            var clash = new Sequfloh.Domain.Execution.SequflohTimeEntry
            {
                TrackingRecordId = a, TrackingNumber = number, UserId = open.UserId, UserName = open.UserName,
                StartedAt = start.AddHours(1), StoppedAt = start.AddHours(3), Seconds = 2 * 3600,
                Events = JsonSerializer.Serialize(new[] { new SequflohTimeService.TimeEvent("Start", start.AddHours(1)), new SequflohTimeService.TimeEvent("Stop", start.AddHours(3)) }, SequflohJson.Options),
            };
            db.SequflohTimeEntries.Add(clash);
            await db.SaveChangesAsync();
            clashId = clash.Id;
        }
        var overlap = await c.PostAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = stop, reason = "forgot to clock off" });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Contains("overlaps", await overlap.Content.ReadAsStringAsync());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SequflohTimeEntries.SingleAsync(x => x.Id == clashId);
            db.SequflohTimeEntries.Remove(row);
            await db.SaveChangesAsync();
        }

        var cb = (await (await c.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = "WO-MADEUP-ADJ", trackingRecordId = a })).JsonAsync()).Id();
        await c.PostOkAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, stoppedAt = stop, reason = "forgot to clock off" });

        var crew = await c.GetElAsync($"{T}/{a}/crew");
        Assert.Equal(2 * 3600, crew.GetProperty("userSeconds").GetInt64());
        var pre = await c.GetElAsync($"/api/sequfloh/chargebacks/prefill/{a}");
        Assert.Equal(2.00m, pre.GetProperty("userHours").GetDecimal());
        var saved = await c.GetElAsync($"/api/sequfloh/chargebacks/{cb}");
        Assert.Equal(2.00m, saved.GetProperty("userHours").GetDecimal());
        Assert.Equal(2.00m, saved.GetProperty("totalHours").GetDecimal());
        Assert.Contains("time correction", saved.Str("timeChangedNote"));
        var record = (await c.GetElAsync($"{T}/{a}")).GetProperty("record");
        Assert.Equal(60m, record.GetProperty("varianceMinutes").GetDecimal());

        var list = (await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("entries").EnumerateArray().Single(e => e.Id() == openId);
        Assert.InRange((list.GetProperty("originalStartedAt").GetDateTimeOffset() - start).Duration().TotalMilliseconds, 0, 2);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("originalStoppedAt").ValueKind);
        Assert.InRange((list.GetProperty("stoppedAt").GetDateTimeOffset() - stop).Duration().TotalMilliseconds, 0, 2);
        Assert.Equal("forgot to clock off", list.Str("adjustReason"));
        Assert.Equal(JsonValueKind.Null, (await c.GetElAsync($"{T}/{a}/time")).GetProperty("people").EnumerateArray().Single().GetProperty("staleNote").ValueKind);

        var later = start.AddHours(1);
        await c.PostOkAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = openId, startedAt = start, stoppedAt = later, reason = "end was an hour early" });
        Assert.Equal(3600, (await c.GetElAsync($"{T}/{a}/crew")).GetProperty("userSeconds").GetInt64());
        Assert.Equal(1.00m, (await c.GetElAsync($"/api/sequfloh/chargebacks/prefill/{a}")).GetProperty("userHours").GetDecimal());
        Assert.Equal(0m, (await c.GetElAsync($"{T}/{a}")).GetProperty("record").GetProperty("varianceMinutes").GetDecimal());
        var kept = (await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("entries").EnumerateArray().Single(e => e.Id() == openId);
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("originalStoppedAt").ValueKind);

        await worker.PostOkAsync($"{T}/{a}/time/start", new { clockIn = true });
        var pausedId = (await c.GetElAsync($"{T}/{a}/time")).GetProperty("entries").EnumerateArray().First(e => e.GetProperty("stoppedAt").ValueKind == JsonValueKind.Null).Id();
        var pauseStart = DateTimeOffset.UtcNow.AddHours(-2);
        await BackdateAsync(pausedId, pauseStart);
        var pausedAt = pauseStart.AddMinutes(30);
        await c.PostOkAsync($"{T}/{a}/time-corrections/adjust", new { timeEntryId = pausedId, pausedAt, reason = "forgot to pause for lunch" });
        var person = (await c.GetElAsync($"{T}/{a}/time")).GetProperty("people").EnumerateArray().Single();
        Assert.InRange(person.GetProperty("seconds").GetInt64(), 3600 + 1790, 3600 + 1810);

        using var auditScope = app.Services.CreateScope();
        var audits = await auditScope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.Where(x => x.EntityId == a && x.Action == "TimeAdjusted").ToListAsync();
        Assert.Contains(audits, x => x.UserName == fixerName && x.Description.Contains($"{fixerName} changed {workerName}'s clock-off on {number} from (open) to ") && x.Description.Contains("Reason: forgot to clock off"));
        Assert.Contains(audits, x => x.Description.Contains("pause on") && x.Description.Contains("Reason: forgot to pause for lunch"));
        Assert.True(audits.All(x => x.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)));
    }
}
