using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class MyWorkTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohReassignTask];
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohStartTask, Permissions.SequflohPauseTask, Permissions.SequflohResumeTask, Permissions.SequflohHoldTask];

    async Task<Guid> ReleasedAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Bracket assembly", Number = "MWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, Steps = """[{"id":"x","sequence":10,"title":"Assemble"}]""", Characteristics = "[]", PartsRows = "[]", Content = "{}" };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    static async Task<JsonElement> Act(ApiClient c, Guid id, string action, object? extra = null)
    {
        var ver = (await c.GetElAsync($"{U}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        var body = new Dictionary<string, object?> { ["operationId"] = Guid.NewGuid(), ["stateVersion"] = ver };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return await (await c.PostOkAsync($"{U}/{id}/actions/{action}", body)).JsonAsync();
    }

    [Fact]
    public async Task My_work_lists_assigned_in_progress_overdue_and_held_work_orders_in_order()
    {
        var site = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (techId, tech) = await app.CreateUserAsync(Tech, site);
        var (_, otherPlanner) = await app.CreateUserAsync(Planner, other);
        var (_, idle) = await app.CreateUserAsync(Tech, site);
        var wi = await ReleasedAsync(site);
        var wiOther = await ReleasedAsync(other);
        var now = DateTimeOffset.UtcNow;

        async Task<Guid> Create(string wo, DateTimeOffset? due) =>
            (await (await planner.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site, workOrder = wo, dueAt = due })).JsonAsync()).Id();
        var later = await Create("WO-LATER", now.AddDays(5));
        var late = await Create("WO-LATE", now.AddDays(-3));
        var held = await Create("WO-HELD", now.AddDays(2));
        var notMine = await Create("WO-OTHER", now.AddDays(1));
        foreach (var id in new[] { later, late, held }) await Act(planner, id, "dispatch", new { assignedUserId = techId });
        await Act(tech, later, "start");
        await Act(tech, held, "start");
        await Act(tech, held, "pause", new { reason = "Waiting on parts" });
        // a work order at a site the person cannot open never shows, even if assigned
        (await (await otherPlanner.PostOkAsync(U, new { type = "Production", workInstructionId = wiOther, siteId = other, workOrder = "WO-FAR" })).JsonAsync()).Id();

        var r = await tech.GetElAsync("/api/sequfloh/my-work");
        var items = r.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["WO-LATE", "WO-HELD", "WO-LATER"], items.Select(x => x.Str("workOrder")).ToArray());
        Assert.True(items[0].GetProperty("overdue").GetBoolean());
        Assert.Equal("Bracket assembly", items[0].Str("description"));
        Assert.False(string.IsNullOrEmpty(items[0].Str("assignedBy")));
        Assert.True(items[2].GetProperty("inProgress").GetBoolean());
        Assert.Contains("Waiting on parts", items[1].GetProperty("constraints")[0].GetString());
        var counts = r.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("assigned").GetInt32());
        Assert.Equal(1, counts.GetProperty("overdue").GetInt32());
        Assert.Equal(1, counts.GetProperty("constrained").GetInt32());
        Assert.DoesNotContain(items, x => x.Str("workOrder") == "WO-OTHER");

        var empty = await idle.GetElAsync("/api/sequfloh/my-work");
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        Assert.Equal(0, empty.GetProperty("counts").GetProperty("assigned").GetInt32());
    }

    [Fact]
    public async Task Notes_are_private_to_their_owner()
    {
        var site = await app.CreateSiteAsync();
        var (_, a) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, b) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        const string N = "/api/sequfloh/my-notes";
        var id = (await (await a.PostOkAsync(N, new { text = "Call the coating supplier", remindOn = "2026-11-03", priority = "Important" })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync(N, new { text = "  " })).StatusCode);

        var mine = (await a.GetElAsync(N)).EnumerateArray().ToList();
        Assert.Single(mine);
        Assert.Equal("Important", mine[0].Str("priority"));
        Assert.Equal("2026-11-03", mine[0].Str("remindOn"));
        Assert.Empty((await b.GetElAsync(N)).EnumerateArray());

        // someone else's note can't be changed or deleted
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsync($"{N}/{id}", new { text = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{N}/{id}")).StatusCode);
        Assert.Equal("Call the coating supplier", (await a.GetElAsync(N))[0].Str("text"));

        Assert.Equal(HttpStatusCode.NoContent, (await a.PutAsync($"{N}/{id}", new { isDone = true, pinned = true })).StatusCode);
        var done = (await a.GetElAsync(N))[0];
        Assert.True(done.GetProperty("isDone").GetBoolean());
        Assert.True(done.GetProperty("pinned").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"{N}/{id}")).StatusCode);
        Assert.Empty((await a.GetElAsync(N)).EnumerateArray());
    }
}
