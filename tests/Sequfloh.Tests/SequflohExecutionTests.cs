using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohExecutionTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string X = "/api/sequfloh/execution";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohViewLiveExecution, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask,
        Permissions.SequflohReassignTask, Permissions.SequflohCancelTask];
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohViewLiveExecution, Permissions.SequflohStartTask, Permissions.SequflohPauseTask, Permissions.SequflohResumeTask,
        Permissions.SequflohHoldTask, Permissions.SequflohCompleteTask];

    async Task<Guid> ReleasedAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Assembly", Number = "TWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, Steps = """[{"id":"x","sequence":10,"title":"Assemble","qualityCheckpoint":"Torque marks"}]""", Characteristics = "[]", PartsRows = "[]", Content = "{}" };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }
    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
    static async Task Act(ApiClient c, Guid id, string action, string? reason = null) =>
        await c.PostOkAsync($"{T}/{id}/actions/{action}", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id), reason });
    static async Task<List<string>> Numbers(ApiClient c, string view, Guid site) =>
        (await c.GetElAsync($"{X}/queue/{view}?site={site}&pageSize=100")).GetProperty("items").EnumerateArray().Select(e => e.Str("number")).ToList();

    [Fact]
    public async Task Live_views_follow_the_stored_state_and_simultaneous_resume_applies_once()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (techId, tech) = await app.CreateUserAsync(Tech, site);
        var (_, tech2) = await app.CreateUserAsync(Tech, site);
        var (_, noLive) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var wi = await ReleasedAsync(site);
        async Task<(Guid Id, string Number)> Task(object? due = null)
        {
            var e = await (await planner.PostOkAsync(T, new { type = "Production", workInstructionId = wi, siteId = site, assignedUserId = techId, dueAt = due, operation = "Final assembly" })).JsonAsync();
            return (e.Id(), e.Str("number"));
        }
        var a = await Task(DateTimeOffset.UtcNow.AddHours(-3));
        var b = await Task(DateTimeOffset.UtcNow.AddDays(2));
        Assert.Equal(HttpStatusCode.Forbidden, (await noLive.GetAsync($"{X}/queue/dispatch")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync($"{X}/queue/everything")).StatusCode);
        Assert.Equal([a.Number, b.Number], await Numbers(tech, "dispatch", site)); // due date first
        Assert.Equal([a.Number], await Numbers(tech, "overdue", site));
        Assert.Contains(a.Number, await Numbers(tech, "mine", site));
        Assert.Empty(await Numbers(tech2, "mine", site));
        var row = (await tech.GetElAsync($"{X}/queue/dispatch?site={site}")).GetProperty("items")[0];
        Assert.Contains(row.GetProperty("actions").EnumerateArray(), x => x.Str("action") == "start");
        Assert.DoesNotContain(row.GetProperty("actions").EnumerateArray(), x => x.Str("action") == "cancel");

        await Act(tech, a.Id, "start");
        Assert.Equal([a.Number], await Numbers(tech, "active", site));
        var station = await Numbers(tech, "station", site);
        Assert.Equal(a.Number, station[0]); // the running task comes first
        var rec = await tech.GetElAsync($"{T}/{a.Id}");
        Assert.Equal("Record the result.", rec.Str("nextAction"));
        await Act(tech, a.Id, "pause", "Material shortage");
        var paused = (await tech.GetElAsync($"{X}/queue/paused?site={site}")).GetProperty("items")[0];
        Assert.Equal("Material shortage", paused.Str("pauseReason"));
        Assert.NotEqual(JsonValueKind.Null, paused.GetProperty("pausedAt").ValueKind);

        // two people resume at the same moment from the same state: one wins, the other is refused
        var v = await Ver(tech, a.Id);
        var r1 = tech.PostAsync($"{T}/{a.Id}/actions/resume", new { operationId = Guid.NewGuid(), stateVersion = v });
        var r2 = tech2.PostAsync($"{T}/{a.Id}/actions/resume", new { operationId = Guid.NewGuid(), stateVersion = v });
        var codes = (await System.Threading.Tasks.Task.WhenAll(r1, r2)).Select(x => x.StatusCode).OrderBy(x => x).ToList();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes);
        rec = await tech.GetElAsync($"{T}/{a.Id}");
        Assert.Equal(1, rec.GetProperty("events").EnumerateArray().Count(e => e.Str("eventType") == "Resume"));

        await Act(planner, b.Id, "cancel", "Order canceled");
        Assert.Equal([b.Number], await Numbers(tech, "finished", site));
        var counts = await tech.GetElAsync($"{X}/counts");
        Assert.True(counts.GetProperty("active").GetInt32() >= 1);

        // work history: task events of visible tasks, filtered
        var events = await tech.GetElAsync($"{X}/events?site={site}&eventType=Pause");
        Assert.Equal(1, events.GetProperty("total").GetInt32());
        Assert.Equal("Material shortage", events.GetProperty("items")[0].Str("reason"));
        Assert.Equal(1, (await tech.GetElAsync($"{X}/events?site={site}&q={b.Number}&eventType=Cancellation")).GetProperty("total").GetInt32());
        var (_, outsider) = await app.CreateUserAsync(Tech, await app.CreateSiteAsync());
        Assert.Equal(0, (await outsider.GetElAsync($"{X}/events?q={a.Number}")).GetProperty("total").GetInt32());
    }
}
