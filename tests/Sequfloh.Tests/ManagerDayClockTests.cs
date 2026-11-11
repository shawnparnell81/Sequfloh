using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A person's manager is chosen on the account, not by a role. That manager can see and correct the report's day clock.</summary>
[Collection("api")]
public sealed class ManagerDayClockTests(TestApp app)
{
    const string D = "/api/sequfloh/day-clock";

    [Fact]
    public async Task An_assigned_manager_can_see_and_correct_a_report_without_the_wide_permissions()
    {
        var admin = await app.AdminAsync();
        var (managerId, manager) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var (workerId, worker) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewOwnDayClock]);
        var (_, other) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var (idleId, _) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var managerRow = await UserAsync(admin, managerId);
        var workerRow = await UserAsync(admin, workerId);
        var managerName = managerRow.GetProperty("displayName").GetString();
        var workerName = workerRow.GetProperty("displayName").GetString();

        var self = await admin.PutAsync($"/api/admin/users/{workerId}", Body(workerRow, workerId, true));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Contains("own manager", await self.Content.ReadAsStringAsync());

        await admin.PutOkAsync($"/api/admin/users/{workerId}", Body(workerRow, managerId, true));
        var listed = await UserAsync(admin, workerId);
        Assert.Equal(managerId, listed.GetProperty("managerId").GetGuid());
        Assert.Equal(managerName, listed.GetProperty("managerName").GetString());

        var back = await admin.PutAsync($"/api/admin/users/{managerId}", Body(managerRow, workerId, true));
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.Contains("circle", await back.Content.ReadAsStringAsync());

        await admin.PutOkAsync($"/api/admin/users/{idleId}", Body(await UserAsync(admin, idleId), false, false));
        var inactive = await admin.PutAsync($"/api/admin/users/{workerId}", Body(workerRow, idleId, true));
        Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
        Assert.Contains("active person", await inactive.Content.ReadAsStringAsync());

        var (thirdId, _) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        await admin.PutOkAsync($"/api/admin/users/{thirdId}", Body(await UserAsync(admin, thirdId), workerId, true));
        var loop = await admin.PutAsync($"/api/admin/users/{managerId}", Body(managerRow, thirdId, true));
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);

        await worker.PostOkAsync($"{D}/in");
        var sheet = await manager.GetElAsync($"{D}/timesheet?userId={workerId}");
        Assert.Equal(workerName, sheet.GetProperty("userName").GetString());
        Assert.NotEqual(JsonValueKind.Null, sheet.GetProperty("days")[0].GetProperty("arrival").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{D}/timesheet?userId={workerId}")).StatusCode);

        var team = await manager.GetElAsync($"{D}/team");
        var report = team.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("userId").GetGuid() == workerId);
        Assert.True(report.GetProperty("clockedIn").GetBoolean());
        Assert.Equal(workerName, report.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{D}/team")).StatusCode);

        var week = await manager.GetElAsync($"{D}/timesheet?userId={workerId}&from={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6)):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}");
        Assert.Equal(7, week.GetProperty("days").GetArrayLength());

        var csv = await manager.GetAsync($"{D}/export.csv?userId={workerId}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Contains(workerName!, await csv.Content.ReadAsStringAsync());
        var reportsOnly = await manager.GetAsync($"{D}/export.csv");
        Assert.Equal(HttpStatusCode.OK, reportsOnly.StatusCode);
        Assert.Contains(workerName!, await reportsOnly.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{D}/export.csv")).StatusCode);

        var start = DateTimeOffset.UtcNow.AddDays(-4).AddHours(-6);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"{D}/corrections", new { reason = "Not their manager", userId = workerId, action = "add-clock", startedAt = start, endedAt = start.AddHours(2) })).StatusCode);
        await manager.PostOkAsync($"{D}/corrections", new { reason = "Bench clock was missed", userId = workerId, action = "add-clock", startedAt = start, endedAt = start.AddHours(2) });
        var history = await manager.GetElAsync($"{D}/history?userId={workerId}");
        Assert.Contains(history.GetProperty("entries").EnumerateArray(), e => (e.GetProperty("description").GetString() ?? "").Contains("Bench clock was missed"));

        await admin.PutOkAsync($"/api/admin/users/{workerId}", Body(await UserAsync(admin, workerId), null, true));
        Assert.Equal(JsonValueKind.Null, (await UserAsync(admin, workerId)).GetProperty("managerId").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{D}/timesheet?userId={workerId}")).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking()
            .Where(a => a.EntityId == workerId && a.Action == "ManagerAssigned").OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Contains("assigned", audits[0].Description);
        Assert.Contains(managerName!, audits[0].Description);
        Assert.Contains("UTC", audits[0].Description);
        Assert.False(string.IsNullOrWhiteSpace(audits[0].UserName));
        Assert.Contains("cleared", audits[1].Description);
        Assert.Contains(workerName!, audits[1].Description);
    }

    static async Task<JsonElement> UserAsync(ApiClient admin, Guid id) =>
        (await admin.GetElAsync("/api/admin/users")).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id);

    static object Body(JsonElement user, Guid? managerId, bool setManager) => new
    {
        displayName = user.GetProperty("displayName").GetString(),
        jobTitle = user.GetProperty("jobTitle").ValueKind == JsonValueKind.Null ? null : user.GetProperty("jobTitle").GetString(),
        defaultSiteId = user.GetProperty("defaultSiteId").ValueKind == JsonValueKind.Null ? (Guid?)null : user.GetProperty("defaultSiteId").GetGuid(),
        isActive = user.GetProperty("isActive").GetBoolean(),
        managerId,
        setManager,
    };

    static object Body(JsonElement user, bool active, bool setManager) => new
    {
        displayName = user.GetProperty("displayName").GetString(),
        jobTitle = user.GetProperty("jobTitle").ValueKind == JsonValueKind.Null ? null : user.GetProperty("jobTitle").GetString(),
        defaultSiteId = user.GetProperty("defaultSiteId").ValueKind == JsonValueKind.Null ? (Guid?)null : user.GetProperty("defaultSiteId").GetGuid(),
        isActive = active,
        setManager,
    };
}
