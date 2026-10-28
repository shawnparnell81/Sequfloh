using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Research work orders: no part number, a product family or Multiple, and a required description. Numbered WO-RSH-{family}{yy}-001. Made-up data only.</summary>
[Collection("api")]
public sealed class ResearchWorkOrderTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task Research_work_orders_need_a_description_and_a_family_or_multiple_not_a_part()
    {
        Assert.Equal(11, (int)TrackingType.Research);
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var famCode = $"R{tag}";
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = famCode, label = $"Research family {tag}", description = (string?)null })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = famCode })).IsSuccessStatusCode);
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
            Permissions.SequflohManageCrewTime, Permissions.SequflohViewDrawings], site);
        var yy = (TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).Year % 100).ToString("00");
        const string desc = "Pallet of mixed returned units: sort, test and say which can be repurposed.";

        // a description is required
        var noDesc = await c.PostAsync(T, new { type = "Research", number = (string?)null, siteId = site, multipleFamilies = true });
        Assert.Equal(HttpStatusCode.BadRequest, noDesc.StatusCode);
        Assert.Contains("description", await noDesc.Content.ReadAsStringAsync());
        // a family or Multiple is required
        var noFam = await c.PostAsync(T, new { type = "Research", number = (string?)null, siteId = site, inspectionDescription = desc });
        Assert.Equal(HttpStatusCode.BadRequest, noFam.StatusCode);
        Assert.Contains("Multiple", await noFam.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(T, new { type = "Research", number = (string?)null, siteId = site, inspectionDescription = desc, familyId = fam, multipleFamilies = true })).StatusCode);

        Assert.Matches($"^WO-RSH-MUL{yy}-\\d{{3}}$", (await c.GetElAsync($"{T}/next-number?type=Research&multiple=true&siteId={site}")).GetProperty("number").GetString()!);
        Assert.Matches($"^WO-RSH-{famCode}{yy}-\\d{{3}}$", (await c.GetElAsync($"{T}/next-number?type=Research&familyId={fam}&siteId={site}")).GetProperty("number").GetString()!);

        // a part sent with the order is ignored: Research has no part number
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", new
        {
            part = new { partNumber = "RS-" + tag, name = "Example part", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
            typeId = (Guid?)null, attributes = (object?)null,
        })).JsonAsync()).Id();
        async Task<System.Text.Json.JsonElement> Create(object body) { var id = (await (await c.PostOkAsync(T, body)).JsonAsync()).Id(); return (await c.GetElAsync($"{T}/{id}")).GetProperty("record"); }
        var (_, settingsAdmin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var tracking = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>((await settingsAdmin.GetElAsync("/api/sequfloh/settings")).GetProperty("tracking").GetRawText())!;
        Assert.Equal("RSH", tracking["researchNumberCode"]?.ToString());
        System.Text.Json.JsonElement m1, m2, f1;
        try
        {
            tracking["researchNumberCode"] = $"q{tag}";
            Assert.Equal(HttpStatusCode.NoContent, (await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 })).StatusCode);
            m1 = await Create(new { type = "Research", number = (string?)null, siteId = site, multipleFamilies = true, inspectionDescription = desc, partId = part, quantity = 40 });
            m2 = await Create(new { type = "Research", number = (string?)null, siteId = site, multipleFamilies = true, inspectionDescription = desc });
            f1 = await Create(new { type = "Research", number = (string?)null, siteId = site, familyId = fam, inspectionDescription = desc });
        }
        finally
        {
            tracking["researchNumberCode"] = "RSH";
            await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 });
        }
        Assert.Equal($"WO-Q{tag}-MUL{yy}-001", m1.GetProperty("number").GetString());
        Assert.Equal($"WO-Q{tag}-MUL{yy}-002", m2.GetProperty("number").GetString());
        Assert.Equal($"WO-Q{tag}-{famCode}{yy}-001", f1.GetProperty("number").GetString());
        Assert.Equal("Multiple", m1.GetProperty("familyName").GetString());
        Assert.Equal(desc, m1.GetProperty("inspectionDescription").GetString());
        Assert.Equal($"Research family {tag}", f1.GetProperty("familyName").GetString());
        // Multiple is not added to the Product Families list
        Assert.DoesNotContain("\"Multiple\"", (await admin.GetElAsync("/api/sequfloh/reference/product-families")).GetRawText());

        var id = m1.Id();
        Assert.Empty((await c.GetElAsync($"/api/drawings/for-record/{id}")).EnumerateArray());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rec = await db.SequflohTrackingRecords.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Null(rec.PartId); Assert.Null(rec.PartNumber); Assert.Null(rec.FamilyId); Assert.Equal(40, rec.QuantityTarget);
            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.Action == "TrackingRecordCreated").Select(a => a.Description).SingleAsync();
            Assert.Contains("(Research)", audit); Assert.Contains("product family Multiple", audit);
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

        // print, job report, chargeback prefill, reports, lists and search work with no part
        foreach (var url in new[] { $"{T}/{id}/print", $"{T}/{id}/job/report", $"{T}/{id}/job/report/pdf", $"/api/sequfloh/chargebacks/prefill/{id}", $"/api/sequfloh/reports/task-performance?type=Research&site={site}&pageSize=50", $"{T}?folder=research&siteId={site}&pageSize=50" })
        {
            var r = await admin.GetAsync(url);
            Assert.True(r.IsSuccessStatusCode, $"{url}: {await r.Content.ReadAsStringAsync()}");
        }
        Assert.Equal(3, (await c.GetElAsync($"{T}?folder=research&siteId={site}&pageSize=50")).GetProperty("items").GetArrayLength());
        Assert.Equal(2, (await c.GetElAsync($"{T}?folder=research&siteId={site}&q=Multiple&pageSize=50")).GetProperty("items").GetArrayLength());
    }
}
