using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Organization;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class ForceDeleteDepartmentTests(TestApp app)
{
    AppDbContext Db(out IServiceScope scope) { scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<AppDbContext>(); }

    [Fact]
    public async Task Force_delete_keeps_a_cancelled_work_order_and_a_user_without_permission_is_blocked()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var dept = await (await admin.PostOkAsync($"/api/organization/sites/{site}/departments", new { code = "QA", name = "Quality cell" })).Content.ReadFromJsonAsync<Guid>();
        Guid cancelled, kept;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gone = new SequflohTrackingRecord { Number = "WO-" + Guid.NewGuid().ToString("N")[..8], Type = TrackingType.Repair, SiteId = site, DepartmentId = dept, Status = TrackingStatus.Canceled, CancelReason = "Made by mistake" };
            var named = new SequflohTrackingRecord { Number = "WO-" + Guid.NewGuid().ToString("N")[..8], Type = TrackingType.Repair, SiteId = site, DepartmentId = dept, DepartmentName = "Quality cell (as written)", Status = TrackingStatus.InProgress };
            db.SequflohTrackingRecords.AddRange(gone, named);
            await db.SaveChangesAsync();
            cancelled = gone.Id;
            kept = named.Id;
        }

        var uses = await admin.GetJsonAsync<JsonElement>($"/api/organization/departments/{dept}/uses");
        Assert.True(uses.GetProperty("blocked").GetBoolean());
        Assert.True(uses.GetProperty("canForce").GetBoolean());
        Assert.Contains(uses.GetProperty("references").EnumerateArray(), r => r.GetProperty("text").GetString() == "2 work orders: 1 cancelled, 1 in progress");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/organization/departments/{dept}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/organization/departments/{dept}/force-delete", new { confirm = "nope" })).StatusCode);

        var (_, limited) = await app.CreateUserAsync([Permissions.AdminOrganization]);
        var denied = await limited.PostAsync($"/api/organization/departments/{dept}/force-delete", new { confirm = "QA" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await limited.DeleteAsync($"/api/organization/departments/{dept}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/organization/departments/{dept}/force-delete", new { confirm = "qa" })).StatusCode);

        var dbAfter = Db(out var s); using var _ = s;
        var order = await dbAfter.SequflohTrackingRecords.SingleAsync(x => x.Id == cancelled);
        Assert.Equal(TrackingStatus.Canceled, order.Status);
        Assert.Null(order.DepartmentId);
        Assert.Equal("Quality cell", order.DepartmentName);
        Assert.Equal("Made by mistake", order.CancelReason);
        var namedOrder = await dbAfter.SequflohTrackingRecords.SingleAsync(x => x.Id == kept);
        Assert.Null(namedOrder.DepartmentId);
        Assert.Equal("Quality cell (as written)", namedOrder.DepartmentName);
        Assert.False(await dbAfter.Departments.AnyAsync(d => d.Id == dept));
        var audit = await dbAfter.AuditEntries.Where(a => a.EntityId == dept && a.Action == "Deleted").SingleAsync();
        Assert.Contains("Department QA", audit.Description);
        Assert.Contains("Records that used it were kept", audit.Description);
        Assert.Equal(nameof(Department), audit.EntityType);
    }
}
