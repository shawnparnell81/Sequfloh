using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Weight work orders: a product family task with no part, drawing or work instruction, numbered WO-WGH-{yy}-001. Made-up data only.</summary>
[Collection("api")]
public sealed class WeightWorkOrderTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task Weight_work_orders_need_a_family_not_a_part_and_run_start_to_complete()
    {
        Assert.Equal(10, (int)TrackingType.Weight);
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"W{tag}", label = $"Weight family {tag}", isActive = true, isDefault = false })).JsonAsync()).Id();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
            Permissions.SequflohManageCrewTime, Permissions.SequflohViewDrawings], site);
        var yy = (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Year % 100).ToString("00");

        // a family is required, a part is not
        var none = await c.PostAsync(T, new { type = "Weight", siteId = site, number = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("product family", await none.Content.ReadAsStringAsync());

        // default code WGH, no family code in the number
        Assert.Matches($"^WO-WGH-{yy}-\\d{{3}}$", (await c.GetElAsync($"{T}/next-number?type=Weight&familyId={fam}&siteId={site}")).GetProperty("number").GetString()!);

        async Task<System.Text.Json.JsonElement> Create() { var id = (await (await c.PostOkAsync(T, new { type = "Weight", siteId = site, familyId = fam, number = (string?)null })).JsonAsync()).Id(); return (await c.GetElAsync($"{T}/{id}")).GetProperty("record"); }
        var (_, settingsAdmin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var tracking = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>((await settingsAdmin.GetElAsync("/api/sequfloh/settings")).GetProperty("tracking").GetRawText())!;
        Assert.Equal("WGH", tracking["weightNumberCode"]?.ToString());
        System.Text.Json.JsonElement r1, r2;
        try
        {
            // a fresh code so the count starts at 001 in a shared test database; editing the code changes new numbers
            tracking["weightNumberCode"] = $"g{tag}";
            Assert.Equal(HttpStatusCode.NoContent, (await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 })).StatusCode);
            r1 = await Create(); r2 = await Create();
        }
        finally
        {
            tracking["weightNumberCode"] = "WGH";
            await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 });
        }
        Assert.Equal($"WO-G{tag}-{yy}-001", r1.GetProperty("number").GetString());
        Assert.Equal($"WO-G{tag}-{yy}-002", r2.GetProperty("number").GetString());
        Assert.Equal("Weight", r1.GetProperty("type").GetString());
        Assert.Equal($"Weight family {tag}", r1.GetProperty("familyName").GetString());
        var r3 = await Create();
        Assert.StartsWith($"WO-WGH-{yy}-", r3.GetProperty("number").GetString());
        Assert.Empty(Sequfloh.Infrastructure.Modules.Execution.SequflohTrackingService.InstructionTypes(TrackingType.Weight));

        // no part, no drawing, no work instruction
        var id = r1.Id();
        Assert.Empty((await c.GetElAsync($"/api/drawings/for-record/{id}")).EnumerateArray());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rec = await db.SequflohTrackingRecords.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Null(rec.PartId); Assert.Null(rec.WorkInstructionId); Assert.Equal(fam, rec.FamilyId);
        }

        static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        async Task Act(string a) { var r = await c.PostAsync($"{T}/{id}/actions/{a}", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id) }); Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); }
        await Act("start");
        await c.PostOkAsync($"{T}/{id}/time/start");
        await c.PostOkAsync($"{T}/{id}/crew/start", new { crewSize = 2 });
        await c.PostOkAsync($"{T}/{id}/time/stop");
        await c.PostOkAsync($"{T}/{id}/crew/stop");
        await Act("complete");
        Assert.Equal("Completed", (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("status").GetString());

        // the job report, the chargeback prefill and the reports work with no part
        foreach (var url in new[] { $"{T}/{id}/job/report", $"/api/sequfloh/chargebacks/prefill/{id}", $"/api/sequfloh/reports/task-performance?type=Weight&site={site}&pageSize=50", $"{T}?folder=weight&siteId={site}&pageSize=50" })
        {
            var r = await admin.GetAsync(url);
            Assert.True(r.IsSuccessStatusCode, $"{url}: {await r.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(3, (await c.GetElAsync($"{T}?folder=weight&siteId={site}&pageSize=50")).GetProperty("items").GetArrayLength());
    }
}
