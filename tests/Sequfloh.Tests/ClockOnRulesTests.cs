using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Clocking on needs a started work order; clocking off and typed-in time stay allowed. Made-up data only.</summary>
[Collection("api")]
public sealed class ClockOnRulesTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Worker = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
        Permissions.SequflohManageCrewTime, Permissions.SequflohDispatchTask];

    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
    static async Task<HttpResponseMessage> Act(ApiClient c, Guid id, string action) =>
        await c.PostAsync($"{T}/{id}/actions/{action}", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id) });

    async Task<(Guid Id, ApiClient C)> NewAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Worker, site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        return (id, c);
    }

    [Fact]
    public async Task Clock_on_is_refused_until_the_work_order_is_started_and_clock_off_still_works()
    {
        var (id, c) = await NewAsync();
        foreach (var r in new[] { await c.PostAsync($"{T}/{id}/time/start", new { }), await c.PostAsync($"{T}/{id}/crew/start", new { crewSize = 2 }) })
        {
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains("Press Start on this work order before clocking on.", await r.Content.ReadAsStringAsync());
        }
        var w = (await (await c.PostOkAsync($"{T}/{id}/crew/workers", new { name = "Day worker A" })).JsonAsync()).GetProperty("workers")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/crew/start", new { crewSize = 1, workerId = w })).StatusCode);

        // typed-in time from earlier is still allowed before Start
        await c.UseCrewTimeAsync(id);
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 2, start = DateTimeOffset.UtcNow.AddHours(-5), stop = DateTimeOffset.UtcNow.AddHours(-4) });

        Assert.True((await Act(c, id, "start")).IsSuccessStatusCode);
        await c.PostOkAsync($"{T}/{id}/crew/start", new { crewSize = 2 });
        await c.PostOkAsync($"{T}/{id}/crew/stop");
        await c.UseCrewTimeAsync(id, false);
        await c.PostOkAsync($"{T}/{id}/time/start");
        await c.PostOkAsync($"{T}/{id}/time/stop");
    }

    [Fact]
    public async Task Time_logged_before_Start_is_kept_when_the_order_is_started_and_completed()
    {
        var (id, c) = await NewAsync();
        await c.UseCrewTimeAsync(id);
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, start = DateTimeOffset.UtcNow.AddHours(-3), stop = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.True((await Act(c, id, "start")).IsSuccessStatusCode);
        var done = await Act(c, id, "complete");
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.SequflohCrewSessions.AsNoTracking().SingleAsync(x => x.TrackingRecordId == id);
        Assert.InRange(s.StoppedAt!.Value - s.StartedAt, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
    }

    [Fact]
    public async Task A_running_timer_reports_its_worked_time_once_and_pausing_keeps_it()
    {
        var (id, c) = await NewAsync();
        Assert.True((await Act(c, id, "start")).IsSuccessStatusCode);
        await c.PostOkAsync($"{T}/{id}/time/start");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var e = await db.SequflohTimeEntries.SingleAsync(x => x.TrackingRecordId == id);
            e.StartedAt = e.StartedAt.AddHours(-2); e.Events = "[]";
            await db.SaveChangesAsync();
        }
        long Secs(System.Text.Json.JsonElement d) => d.GetProperty("people")[0].GetProperty("seconds").GetInt64();
        var running = await c.GetElAsync($"{T}/{id}/time");
        Assert.InRange(Secs(running), 7190, 7260);
        var paused = await (await c.PostOkAsync($"{T}/{id}/time/pause", new { reason = "Break" })).JsonAsync();
        Assert.InRange(Secs(paused), 7190, 7260);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, paused.GetProperty("people")[0].GetProperty("runningSince").ValueKind);
    }
}

static class WorkOrderStart
{
    /// <summary>Presses Start on a work order, as a person must before clocking on.</summary>
    public static async Task StartAsync(this ApiClient c, Guid id)
    {
        var v = (await c.GetElAsync($"/api/sequfloh/tracking/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        await c.PostOkAsync($"/api/sequfloh/tracking/{id}/actions/start", new { operationId = Guid.NewGuid(), stateVersion = v });
    }
}
