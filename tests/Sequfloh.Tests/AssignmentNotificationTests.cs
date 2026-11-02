using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class AssignmentNotificationTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    const string N = "/api/notifications";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohReassignTask];
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohStartTask];

    async Task<Guid> ReleasedAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Gearbox build", Number = "NWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, Steps = """[{"id":"x","sequence":10,"title":"Assemble"}]""", Characteristics = "[]", PartsRows = "[]", Content = "{}" };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    static async Task<List<JsonElement>> Mine(ApiClient c) => (await c.GetElAsync(N)).EnumerateArray().ToList();
    static async Task<int> Unread(ApiClient c) => (await c.GetElAsync($"{N}/unread-count")).GetProperty("count").GetInt32();

    [Fact]
    public async Task Newly_assigned_lead_and_team_members_are_told_once_and_only_they_can_read_it()
    {
        var site = await app.CreateSiteAsync();
        var (plannerId, planner) = await app.CreateUserAsync(Planner, site);
        var (leadId, lead) = await app.CreateUserAsync(Tech, site);
        var (mateId, mate) = await app.CreateUserAsync(Tech, site);
        var (_, outsider) = await app.CreateUserAsync(Tech, site);
        var wi = await ReleasedAsync(site);
        string plannerName;
        using (var scope = app.Services.CreateScope())
            plannerName = scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Single(u => u.Id == plannerId).DisplayName;

        // created with a lead: the lead is told, by whom and what the work is; the planner who assigned is not
        var rec = await (await planner.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site, workOrder = "WO-N1", assignedUserId = leadId })).JsonAsync();
        var id = rec.Id();
        var number = rec.GetProperty("number").GetString();
        var leadNotes = await Mine(lead);
        var n = Assert.Single(leadNotes, x => x.GetProperty("recordId").GetString() == id.ToString());
        Assert.Equal($"{number} was assigned to you by {plannerName}: Gearbox build", n.GetProperty("title").GetString());
        Assert.Equal($"/sequfloh/record/{id}", n.GetProperty("link").GetString());
        Assert.DoesNotContain(await Mine(planner), x => x.GetProperty("recordId").GetString() == id.ToString());
        Assert.Equal(1, await Unread(lead));

        // a team member added later is told; the lead, still on the work order, is not told again; an unchanged save sends nothing
        await planner.PutOkAsync($"{U}/{id}/assignees", new { userIds = new[] { leadId, mateId } });
        await planner.PutOkAsync($"{U}/{id}/assignees", new { userIds = new[] { leadId, mateId } });
        Assert.Single(await Mine(mate), x => x.GetProperty("recordId").GetString() == id.ToString());
        Assert.Single(await Mine(lead), x => x.GetProperty("recordId").GetString() == id.ToString());
        // the planner putting themself on the team is not told about their own assignment
        await planner.PutOkAsync($"{U}/{id}/assignees", new { userIds = new[] { leadId, mateId, plannerId } });
        Assert.DoesNotContain(await Mine(planner), x => x.GetProperty("recordId").GetString() == id.ToString());

        // one person can't read or mark another person's notifications
        var noteId = n.GetProperty("id").GetString();
        Assert.Empty(await Mine(outsider));
        Assert.Equal(0, await Unread(outsider));
        await outsider.PostAsync($"{N}/{noteId}/read", new { });
        await outsider.PostAsync($"{N}/read-all", new { });
        Assert.Equal(1, await Unread(lead));
        Assert.Equal(1, await Unread(mate));

        // mark all read only clears the caller's own
        Assert.Equal(HttpStatusCode.NoContent, (await lead.PostAsync($"{N}/read-all", new { })).StatusCode);
        Assert.Equal(0, await Unread(lead));
        Assert.Equal(1, await Unread(mate));
        Assert.Equal(HttpStatusCode.NoContent, (await mate.PostAsync($"{N}/{(await Mine(mate))[0].GetProperty("id").GetString()}/read", new { })).StatusCode);
        Assert.Equal(0, await Unread(mate));

        // signed out, nothing
        var anon = await app.AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"{N}/unread-count")).StatusCode);
    }
}
