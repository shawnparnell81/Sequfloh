using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>The new work order builder: validation forms by product family, inspection and warranty work orders and their results, and no overall result. Made-up data only.</summary>
[Collection("api")]
public sealed class FixList2Tests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles, Permissions.SequflohViewLiveExecution];

    static async Task<Guid> BlankAsync(ApiClient c, Guid site, string fileName)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,\n")), "file", fileName);
        form.Add(new StringContent(site.ToString()), "siteId");
        form.Add(new StringContent("true"), "blankForm");
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        return (await up.JsonAsync()).Id();
    }

    [Fact]
    public async Task A_validation_work_order_gets_only_the_confirmed_form_of_its_family()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var famId = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "GZ" + tag, label = "Gizmo " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();
        var blank = await BlankAsync(c, site, $"Gizmo {tag} validation sheet.csv");
        var other = await BlankAsync(c, site, $"Gizmo {tag} packing list.csv");

        var row = (await c.GetElAsync("/api/sequfloh/validation-forms")).GetProperty("families").EnumerateArray().First(f => f.GetProperty("id").GetGuid() == famId);
        Assert.Equal(blank, row.GetProperty("suggestedId").GetGuid());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("blankFormId").ValueKind); // a suggestion is never saved on its own

        var r1 = await (await c.PostOkAsync(T, new { type = "Validation", siteId = site, familyId = famId, quantity = 4 })).JsonAsync();
        Assert.Contains("No validation form is confirmed", r1.GetProperty("validationForm").GetProperty("message").GetString());
        var rec1 = (await c.GetElAsync($"{T}/{r1.Id()}")).GetProperty("record");
        Assert.Equal(4, rec1.GetProperty("quantityTarget").GetInt32());
        Assert.Contains("No validation form", rec1.GetProperty("details").GetProperty("validationForm").GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsync($"/api/sequfloh/validation-forms/{famId}", new { blankFormId = blank })).StatusCode);
        Assert.True((await admin.PutAsync($"/api/sequfloh/validation-forms/{famId}", new { blankFormId = blank })).IsSuccessStatusCode);
        row = (await c.GetElAsync("/api/sequfloh/validation-forms")).GetProperty("families").EnumerateArray().First(f => f.GetProperty("id").GetGuid() == famId);
        Assert.Equal(blank, row.GetProperty("blankFormId").GetGuid());

        var r2 = await (await c.PostOkAsync(T, new { type = "Validation", siteId = site, familyId = famId })).JsonAsync();
        var fileId = Guid.Parse(r2.GetProperty("validationForm").GetProperty("fileId").GetString()!);
        var file = await c.GetElAsync($"/api/imported-files/{fileId}");
        Assert.False(file.GetProperty("isBlankForm").GetBoolean());
        Assert.Equal(famId, file.GetProperty("familyId").GetGuid());
        Assert.Equal(blank, file.GetProperty("blankForm").GetProperty("id").GetGuid());
        Assert.NotEqual(other, fileId);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == famId).ToListAsync();
        Assert.Contains(audit, a => a.Description.Contains($"Validation form for Gizmo {tag} changed from none to") && a.UserName.Length > 0);
    }

    [Fact]
    public async Task Inspection_and_warranty_work_orders_keep_sub_type_description_and_results()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var subs = (await c.GetElAsync("/api/sequfloh/reference/inspection-sub-types/options")).EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["AQL", "Supplier request", "Warranty claim", "Product alert", "Non-conformance"], subs);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "Not on the list" })).StatusCode);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", inspectionDescription = "Check 20 made-up housings for flash", quantity = 20 })).JsonAsync()).Id();
        var w = (await (await c.PostOkAsync(T, new { type = "Warranty", siteId = site, inspectionSubType = "Warranty claim", inspectionDescription = "Made-up claim 77" })).JsonAsync()).Id();
        var rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal("AQL", rec.Str("inspectionSubType"));
        Assert.Equal("Check 20 made-up housings for flash", rec.Str("inspectionDescription"));
        var wr = (await c.GetElAsync($"{T}/{w}")).GetProperty("record");
        Assert.Equal("Warranty", wr.Str("type"));
        Assert.Contains((await c.GetElAsync($"{T}?folder=warranty&site={site}")).GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == w);
        Assert.Contains((await c.GetElAsync($"{T}?folder=inspection&site={site}")).GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync($"{T}/{id}/inspection-results", new { qtyGood = 17, qtyBad = 3, furtherInspection = true, quarantine = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync($"{T}/{id}/inspection-results", new { qtyGood = -1, quarantine = false })).StatusCode);
        Assert.True((await c.PutAsync($"{T}/{id}/inspection-results", new { qtyGood = 17, qtyBad = 3, furtherInspection = true, quarantine = true, quarantineReason = "Flash on the sealing face" })).IsSuccessStatusCode);
        rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal(17, rec.GetProperty("qtyGood").GetInt32());
        Assert.Equal(3, rec.GetProperty("qtyBad").GetInt32());
        Assert.True(rec.GetProperty("furtherInspection").GetBoolean());
        Assert.True(rec.GetProperty("quarantine").GetBoolean());
        Assert.Equal("Flash on the sealing face", rec.Str("quarantineReason"));

        // qty bad feeds the chargeback report
        var pre = await (await app.AdminAsync()).GetElAsync($"/api/sequfloh/chargebacks/prefill/{id}");
        Assert.Equal(3, pre.GetProperty("qtyBad").GetInt32());
        Assert.Equal(20, pre.GetProperty("qtyInspected").GetInt32());

        // results are kept only on inspection and warranty work orders
        var prod = (await (await c.PostOkAsync(T, new { type = "Production", siteId = site })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync($"{T}/{prod}/inspection-results", new { qtyGood = 1, quarantine = false })).StatusCode);

        var hist = (await (await app.AdminAsync()).GetElAsync($"{T}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(hist, h => h.Contains("inspection results changed from") && h.Contains("quarantine yes (Flash on the sealing face)"));
    }

    [Fact]
    public async Task A_work_order_completes_without_an_overall_result_and_reports_have_no_result_columns()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Planner, Permissions.SequflohViewReports], site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        async Task<int> Ver() => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        Assert.True((await c.PostAsync($"{T}/{id}/actions/start", new { operationId = Guid.NewGuid(), stateVersion = await Ver() })).IsSuccessStatusCode);
        var problems = (await c.GetElAsync($"{T}/{id}")).GetProperty("completionProblems").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.DoesNotContain(problems, p => p!.Contains("result"));
        var done = await c.PostAsync($"{T}/{id}/actions/complete", new { operationId = Guid.NewGuid(), stateVersion = await Ver() });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());

        foreach (var report in new[] { "task-performance", "repair", "validation" })
        {
            var cols = (await c.GetElAsync($"/api/sequfloh/reports/{report}")).GetProperty("columns").EnumerateArray().Select(x => x.Str("key")).ToList();
            Assert.DoesNotContain("result", cols);
            Assert.DoesNotContain("testStatus", cols);
        }
    }

    [Fact]
    public void Repair_test_is_no_longer_offered_as_an_instruction_type()
    {
        Assert.DoesNotContain(Sequfloh.Infrastructure.Modules.Execution.SequflohCatalog.Lists.First(l => l.Key == "work-instruction-types").Seed, s => s == "Repair Test");
    }
}
