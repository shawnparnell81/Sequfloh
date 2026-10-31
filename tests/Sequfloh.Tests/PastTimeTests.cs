using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Add past time for a registered person: permission, validation, how it counts, audit. Made-up data only.</summary>
[Collection("api")]
public sealed class PastTimeTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Base = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime, Permissions.SequflohViewReports,
        Permissions.SequflohDispatchTask, Permissions.SequflohManageChargebacks];
    static readonly string[] Adder = [.. Base, Permissions.SequflohAddPastTime, Permissions.SequflohRemoveTime];
    const string Why = "Rebuilt the made-up strut mount; tablet was down so nobody clocked on";

    static async Task<Guid> NewAsync(ApiClient c, Guid site)
    {
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await c.StartAsync(id);
        return id;
    }
    static DateTimeOffset Day(int back) => new(DateTimeOffset.UtcNow.AddDays(-back).Date.AddHours(11), TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("forgot to clock")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("test test test test test test")]
    [InlineData("asdf asdf asdf asdf asdf")]
    public void Short_repeated_or_placeholder_reasons_are_rejected(string reason) => Assert.NotNull(SequflohTimeCorrectionService.CheckDetailedReason(reason));

    [Fact]
    public void A_detailed_reason_is_accepted() => Assert.Null(SequflohTimeCorrectionService.CheckDetailedReason(Why));

    [Fact]
    public async Task Past_time_needs_the_permission_and_counts_like_clocked_time_once_per_person()
    {
        var site = await app.CreateSiteAsync();
        var (adderId, c) = await app.CreateUserAsync(Adder, site);
        var (otherId, plain) = await app.CreateUserAsync(Base, site);
        var a = await NewAsync(c, site); var b = await NewAsync(c, site);
        var start = Day(2);

        // gating
        Assert.False((await plain.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("canAddPast").GetBoolean());
        Assert.True((await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("canAddPast").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{T}/{a}/time/past", new { userId = otherId, start, stop = start.AddHours(2), reason = Why })).StatusCode);

        // a chargeback linked before the time was added
        var cb = (await (await c.PostOkAsync("/api/sequfloh/chargebacks", new { workOrderNumber = "WO-MADEUP-PT", trackingRecordId = a })).JsonAsync()).Id();

        // validation
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{a}/time/past", new { start, stop = start.AddHours(2), reason = "forgot" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{a}/time/past", new { start, stop = start.AddHours(2), reason = "test test test test test test" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{a}/time/past", new { start, stop = start, reason = Why })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{a}/time/past", new { start = DateTimeOffset.UtcNow.AddHours(-1), stop = DateTimeOffset.UtcNow.AddHours(1), reason = Why })).StatusCode);

        // 7:00-13:30 with a 30 minute lunch = 6.00 h for the adder (no userId = yourself)
        await c.PostOkAsync($"{T}/{a}/time/past", new { start, stop = start.AddHours(6.5), pauses = new[] { new { from = start.AddHours(3), to = start.AddHours(3.5), reason = "Lunch" } }, reason = Why });
        // the same person clocks normally too, and another person gets past time
        await c.PostOkAsync($"{T}/{a}/time/start"); await c.PostOkAsync($"{T}/{a}/time/stop");
        await c.PostOkAsync($"{T}/{a}/time/past", new { userId = otherId, start, stop = start.AddHours(2), reason = Why });

        var crew = await c.GetElAsync($"{T}/{a}/crew");
        Assert.InRange(crew.GetProperty("userSeconds").GetInt64(), 8 * 3600, 8 * 3600 + 60);
        Assert.Equal(2, crew.GetProperty("personnel").GetInt32());
        var pre = await c.GetElAsync($"/api/sequfloh/chargebacks/prefill/{a}");
        Assert.Equal(2, pre.GetProperty("totalPersonnel").GetInt32());
        Assert.Equal(8.0m, Math.Round(pre.GetProperty("userHours").GetDecimal()));
        var cbEl = await c.GetElAsync($"/api/sequfloh/chargebacks/{cb}");
        Assert.Contains("past time added", cbEl.Str("timeChangedNote"));

        // log marks it, corrections show it
        var log = (await c.GetElAsync($"{T}/{a}/time")).GetProperty("log").EnumerateArray().ToList();
        Assert.Contains(log, l => l.GetProperty("added").GetBoolean() && l.Str("label")!.Contains($"added after the fact by") && l.Str("label")!.Contains(Why));
        var fixes = (await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("entries").EnumerateArray().Where(e => e.Str("note")?.StartsWith("Added after the fact by") == true).ToList();
        Assert.Equal(2, fixes.Count);

        // overlap on the same work order is rejected; on another work order it is only a warning
        var clash = await c.PostAsync($"{T}/{a}/time/past", new { start = start.AddHours(1), stop = start.AddHours(2), reason = Why });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("already has time on", await clash.Content.ReadAsStringAsync());
        var warn = await c.GetElAsync($"{T}/{b}/time/past/overlaps?userId={adderId}&start={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}&stop={Uri.EscapeDataString(start.AddHours(2).ToString("o"))}");
        Assert.Equal(1, warn.GetArrayLength());
        await c.PostOkAsync($"{T}/{b}/time/past", new { start = start.AddHours(1), stop = start.AddHours(2), reason = Why });

        // audit
        using (var s = app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var audits = await db.AuditEntries.Where(x => x.EntityId == a && x.Action == "PastTimeAdded").ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.Contains(audits, x => x.Description!.Contains("added 6.00 h of past time for") && x.Description.Contains($"reason: {Why}") && x.UserName != null);
        }

        // it can be removed with the existing correction tool
        await c.PostOkAsync($"{T}/{a}/time-corrections/remove", new { timeEntryIds = fixes.Select(f => f.Id()).ToArray(), reason = "Made-up removal" });
        Assert.InRange((await c.GetElAsync($"{T}/{a}/crew")).GetProperty("userSeconds").GetInt64(), 0, 60);
    }

    [Fact]
    public async Task Past_time_is_blocked_when_the_work_order_uses_crew_time()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Adder, site);
        var a = await NewAsync(c, site);
        using (var s = app.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
        Assert.True((await c.GetElAsync($"{T}/{a}/time-corrections")).GetProperty("useCrewTime").GetBoolean());
        var start = Day(1);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{a}/time/past", new { start, stop = start.AddHours(1), reason = Why })).StatusCode);
    }
}
