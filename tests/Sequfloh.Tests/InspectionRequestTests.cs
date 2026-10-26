using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class InspectionRequestTests(TestApp app)
{
    const string U = "/api/sequfloh/inspection-requests";

    async Task<(Guid Part, string Pn, Guid Wi)> SetupAsync(Guid site)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = new Part { PartNumber = "IRP-" + Guid.NewGuid().ToString("N")[..8], Name = "Made-up bracket", SiteId = site, Status = MasterStatus.Active };
        db.Parts.Add(p);
        var wi = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Made-up inspection", Number = "IWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return (p.Id, p.PartNumber, wi.Id);
    }

    [Fact]
    public async Task Anyone_can_submit_and_the_list_is_ordered_by_priority_then_date()
    {
        var site = await app.CreateSiteAsync();
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (part, pn, _) = await SetupAsync(site);
        var low = (await (await plain.PostOkAsync(U, new { siteId = site, partNumber = "FREE-1", priority = "Low", requestedDate = "2026-10-01" })).JsonAsync()).Id();
        var high = (await (await plain.PostOkAsync(U, new { siteId = site, partNumber = "FREE-2", priority = "High", requestedDate = "2026-12-01" })).JsonAsync()).Id();
        var crit = await (await plain.PostOkAsync(U, new { siteId = site, partId = part, quantity = 25m, reason = "Warranty claim", priority = "Critical", requestedDate = "2026-12-05", requirement = "Check welds" })).JsonAsync();
        Assert.Equal(pn, crit.GetProperty("partNumber").GetString());
        Assert.Equal("Made-up bracket", crit.GetProperty("partDescription").GetString()); // filled from the part
        var high2 = (await (await plain.PostOkAsync(U, new { siteId = site, partNumber = "FREE-3", priority = "High", requestedDate = "2026-11-01" })).JsonAsync()).Id();
        var list = (await plain.GetElAsync($"{U}?status=active")).EnumerateArray().Select(x => x.Id()).Where(i => i == low || i == high || i == high2 || i == crit.Id()).ToList();
        Assert.Equal([crit.Id(), high2, high, low], list);
        Assert.True((await plain.GetElAsync($"{U}/{low}")).GetProperty("overdue").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsync(U, new { siteId = site, partNumber = "X", reason = "Made up" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsync(U, new { siteId = site, partNumber = " " })).StatusCode);
        await plain.PostOkAsync($"{U}/{low}/status", new { status = "Cancelled" });
        Assert.Equal("Cancelled", (await plain.GetElAsync($"{U}/{low}")).GetProperty("status").GetString());
        var hist = (await (await app.AdminAsync()).GetElAsync($"{U}/{low}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString()).ToList();
        Assert.Contains(hist, h => h!.Contains("Open → Cancelled"));
    }

    [Fact]
    public async Task A_request_becomes_a_work_order_with_its_fields_and_can_be_deleted()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohDeleteRecords], site);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (part, _, wi) = await SetupAsync(site);
        var id = (await (await plain.PostOkAsync(U, new { siteId = site, partId = part, quantity = 40m, reason = "AQL sample result", priority = "Critical", requestedDate = "2026-11-20", requirement = "Check welds" })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/convert", new { workInstructionId = wi, type = "Production" })).StatusCode);
        var wo = await (await planner.PostOkAsync($"{U}/{id}/convert", new { workInstructionId = wi, type = "Production" })).JsonAsync();
        var woId = wo.GetProperty("workOrderId").GetGuid();
        var rec = (await planner.GetElAsync($"/api/sequfloh/tracking/{woId}")).GetProperty("record");
        Assert.Equal("Urgent", rec.GetProperty("priority").GetString());
        Assert.Contains("Check welds", rec.GetProperty("reportedCondition").GetString());
        Assert.Equal(40, (await planner.GetElAsync($"/api/sequfloh/tracking/{woId}/job")).GetProperty("quantityTarget").GetInt32());
        var req = await plain.GetElAsync($"{U}/{id}");
        Assert.Equal("InProgress", req.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await planner.PostAsync($"{U}/{id}/convert", new { workInstructionId = wi })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync($"{U}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await planner.DeleteAsync($"{U}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync($"{U}/{id}")).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == id && a.Action.Contains("Delete")));
        Assert.True(await db.SequflohTrackingRecords.AnyAsync(r => r.Id == woId));
    }

    [Fact]
    public async Task A_converted_request_defaults_to_an_inspection_work_order()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask], site);
        var (part, _, wi) = await SetupAsync(site);
        var id = (await (await planner.PostOkAsync(U, new { siteId = site, partId = part, quantity = 12m, priority = "Normal" })).JsonAsync()).Id();
        var woId = (await (await planner.PostOkAsync($"{U}/{id}/convert", new { workInstructionId = wi })).JsonAsync()).GetProperty("workOrderId").GetGuid();
        Assert.Equal("Inspection", (await planner.GetElAsync($"/api/sequfloh/tracking/{woId}")).GetProperty("record").GetProperty("type").GetString());
        var id2 = (await (await planner.PostOkAsync(U, new { siteId = site, partId = part, priority = "Normal" })).JsonAsync()).Id();
        var wo2 = (await (await planner.PostOkAsync($"{U}/{id2}/convert", new { workInstructionId = wi, type = "Inspection" })).JsonAsync()).GetProperty("workOrderId").GetGuid();
        Assert.Contains(wo2.ToString(), (await planner.GetElAsync("/api/sequfloh/tracking?folder=inspection")).GetRawText());
        Assert.DoesNotContain(wo2.ToString(), (await planner.GetElAsync("/api/sequfloh/tracking?folder=production")).GetRawText());
    }

    [Fact]
    public async Task A_request_can_be_assigned_reassigned_and_cancelled_from_the_queue()
    {
        var site = await app.CreateSiteAsync();
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, lead) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohDispatchTask, Permissions.SequflohReassignTask, Permissions.SequflohCreateProductionTask], site);
        var (a, _) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (b, _) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (part, _, _) = await SetupAsync(site);
        var id = (await (await plain.PostOkAsync(U, new { siteId = site, partId = part, priority = "High", number = "IRA-" + Guid.NewGuid().ToString("N")[..6] })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync($"{U}/{id}/assign", new { userId = a })).StatusCode);
        Assert.Equal(a, (await (await lead.PostOkAsync($"{U}/{id}/assign", new { userId = a })).JsonAsync()).GetProperty("assignedUserId").GetGuid());
        var one = await plain.GetElAsync($"{U}/{id}");
        Assert.Equal(a, one.GetProperty("assignedUserId").GetGuid());
        await lead.PostOkAsync($"{U}/{id}/assign", new { userId = b });
        Assert.Equal(b, (await plain.GetElAsync($"{U}/{id}")).GetProperty("assignedUserId").GetGuid());
        var wo = (await (await lead.PostOkAsync($"{U}/{id}/convert", new { type = "Inspection", number = "WO-IRA-" + Guid.NewGuid().ToString("N")[..6] })).JsonAsync()).GetProperty("workOrderId").GetGuid();
        using (var scope = app.Services.CreateScope())
            Assert.Equal(b, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTrackingRecords.FirstAsync(x => x.Id == wo)).AssignedUserId);
        await plain.PostOkAsync($"{U}/{id}/status", new { status = "Cancelled" });
        Assert.Equal("Cancelled", (await plain.GetElAsync($"{U}/{id}")).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{U}/{id}/assign", new { userId = a })).StatusCode);
        var hist = (await (await app.AdminAsync()).GetElAsync($"{U}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString()).ToList();
        Assert.Contains(hist, h => h!.StartsWith("Assigned ") && h.Contains(" to "));
        Assert.Contains(hist, h => h!.StartsWith("Reassigned "));
        Assert.Contains(hist, h => h!.Contains("→ Cancelled"));
    }
}
