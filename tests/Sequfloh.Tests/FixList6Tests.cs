using System.Net;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>A completed work order is locked, and only roles given the reopen permission can open it again. Made-up data only.</summary>
[Collection("api")]
public sealed class FixList6Tests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Worker = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
        Permissions.SequflohManageCrewTime, Permissions.SequflohDispatchTask];

    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();

    static async Task<HttpStatusCode> Act(ApiClient c, Guid id, string action, string? reason = null) =>
        (await c.PostAsync($"{T}/{id}/actions/{action}", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id), reason })).StatusCode;

    async Task<(Guid Id, ApiClient C, Guid Site)> CompletedAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Worker, site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "start"));
        await c.PostOkAsync($"{T}/{id}/crew/start", new { crewSize = 3 });
        var done = await c.PostAsync($"{T}/{id}/actions/complete", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id) });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        return (id, c, site);
    }

    [Fact]
    public async Task A_completed_work_order_can_not_be_changed()
    {
        var (id, c, _) = await CompletedAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/time/start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/crew/sessions", new { crewSize = 2, startedAt = DateTimeOffset.UtcNow.AddHours(-30), stoppedAt = DateTimeOffset.UtcNow.AddHours(-29) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/crew/workers", new { name = "Day worker A" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/job/quantity", new { completed = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsync($"{T}/{id}/job/cost-lines", new[] { new { description = "Seal kit", amount = 5m } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsync($"{T}/{id}/assignees", new { userIds = Array.Empty<Guid>() })).StatusCode);
        // a crew still clocked on can be clocked off so its time is kept
        await c.PostOkAsync($"{T}/{id}/crew/stop", new { });
        var crew = await c.GetElAsync($"{T}/{id}/crew");
        Assert.False(crew.GetProperty("canEdit").GetBoolean());
        // reading still works
        Assert.Equal("Completed", (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_the_reopen_permission_reopens_with_a_reason_and_history()
    {
        var (id, c, site) = await CompletedAsync();
        Assert.Equal(HttpStatusCode.Forbidden, await Act(c, id, "reopen", "Missed a step"));
        var (_, mgr) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohReopenCompleted, Permissions.SequflohViewAuditTrail], site);
        Assert.Contains((await mgr.GetElAsync($"{T}/{id}")).GetProperty("actions").EnumerateArray(), a => a.GetProperty("action").GetString() == "reopen");
        Assert.Equal(HttpStatusCode.BadRequest, await Act(mgr, id, "reopen"));
        Assert.Equal(HttpStatusCode.OK, await Act(mgr, id, "reopen", "Missed a step"));
        Assert.Equal("InProgress", (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("status").GetString());
        await c.PostOkAsync($"{T}/{id}/job/quantity", new { completed = 1 });
        var hist = (await mgr.GetElAsync($"{T}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(hist, h => h.Contains("reopen"));
    }

    [Fact]
    public async Task A_canceled_work_order_can_not_be_reopened_or_changed()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Worker, Permissions.SequflohCancelTask, Permissions.SequflohReopenCompleted], site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL" })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "cancel", "Made by mistake"));
        Assert.DoesNotContain((await c.GetElAsync($"{T}/{id}")).GetProperty("actions").EnumerateArray(), a => a.GetProperty("action").GetString() == "reopen");
        Assert.Equal(HttpStatusCode.Conflict, await Act(c, id, "reopen", "Try"));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{id}/job/quantity", new { completed = 1 })).StatusCode);
    }
}
