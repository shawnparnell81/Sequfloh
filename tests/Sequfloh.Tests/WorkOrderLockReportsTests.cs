using System.Net;
using System.Text;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>A closed work order locks its form copy, and completing a work order saves its job report to Reports. Made-up data only.</summary>
[Collection("api")]
public sealed class WorkOrderLockReportsTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string Setting = "/api/sequfloh/validation-forms/tests/salt-spray";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohCancelTask, Permissions.SequflohImportFiles, Permissions.SequflohViewImportedFiles];

    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
    static async Task<HttpStatusCode> Act(ApiClient c, Guid id, string action, string? reason = null) =>
        (await c.PostAsync($"{T}/{id}/actions/{action}", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id), reason, varianceReason = "Test" })).StatusCode;

    [Fact]
    public async Task The_form_copy_of_a_closed_work_order_is_locked_without_the_reopen_permission()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var (_, mgr) = await app.CreateUserAsync([.. Planner, Permissions.SequflohReopenCompleted], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Hours,Result\n96,\n")), "file", $"Salt Spray Lock {tag}.csv" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var blank = (await up.JsonAsync()).Id();
        try
        {
            Assert.True((await admin.PutAsync(Setting, new { blankFormId = blank })).IsSuccessStatusCode);
            var a = await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site, number = $"LK-{tag}-1" })).JsonAsync();
            var copy = Guid.Parse(a.GetProperty("validationForm").GetProperty("fileId").GetString()!);
            var open = await c.PutAsync($"/api/imported-files/{copy}/title", new { title = $"Open {tag}" }); Assert.True(open.IsSuccessStatusCode, $"{open.StatusCode} {await open.Content.ReadAsStringAsync()}");
            Assert.Equal(HttpStatusCode.OK, await Act(c, a.Id(), "cancel", "Not needed"));
            Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsync($"/api/imported-files/{copy}/title", new { title = $"Closed {tag}" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/imported-files/{copy}/archive", new { })).StatusCode);
            Assert.False((await c.GetElAsync($"/api/imported-files/{copy}")).GetProperty("canEdit").GetBoolean());
            Assert.True((await mgr.PutAsync($"/api/imported-files/{copy}/title", new { title = $"Fixed {tag}" })).IsSuccessStatusCode);
        }
        finally { await admin.PutAsync(Setting, new { blankFormId = (Guid?)null }); }
    }

    [Fact]
    public async Task Completing_saves_a_job_report_that_only_report_viewers_can_find_by_work_order_number()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Planner, Permissions.SequflohReopenCompleted], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewReports], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", number = $"JR-{tag}", workOrder = $"WOJR-{tag}", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "start"));
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "complete"));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.GetAsync($"/api/sequfloh/saved-reports?q=JR-{tag}")).StatusCode);
        var list = (await viewer.GetElAsync($"/api/sequfloh/saved-reports?q=JR-{tag}&type=job")).EnumerateArray().ToList();
        Assert.Single(list);
        Assert.Equal("job", list[0].GetProperty("type").GetString());
        Assert.Empty((await viewer.GetElAsync($"/api/sequfloh/saved-reports?q=NOPE-{tag}")).EnumerateArray());
        var pdf = await viewer.Http.GetAsync(list[0].GetProperty("url").GetString()!);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Http.GetAsync(list[0].GetProperty("url").GetString()!)).StatusCode);

        // reopened and completed again: a second report, the first one kept
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "reopen", "Missed a step"));
        Assert.Equal(HttpStatusCode.OK, await Act(c, id, "complete"));
        Assert.Equal(2, (await viewer.GetElAsync($"/api/sequfloh/saved-reports?q=JR-{tag}&type=job")).EnumerateArray().Count());
    }
}
