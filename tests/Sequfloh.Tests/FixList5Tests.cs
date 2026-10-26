using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A blank form and a PDF can be renamed (number and title), and Salt Spray Test work orders use the form confirmed for the test. Made-up data only.</summary>
[Collection("api")]
public sealed class FixList5Tests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles, Permissions.SequflohViewLiveExecution];

    static async Task<Guid> UploadAsync(ApiClient c, Guid site, string fileName, byte[] bytes, string flag)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), flag } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        return (await up.JsonAsync()).Id();
    }

    [Fact]
    public async Task A_blank_form_and_a_pdf_can_change_number_and_title_with_an_audit()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var blank = await UploadAsync(c, site, $"Fuel pump validation {tag}.csv", Encoding.UTF8.GetBytes("Check,Result\nTorque,\n"), "blankForm");
        var pdf = await UploadAsync(c, site, $"Old pdf {tag}.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"), "completedForm");
        foreach (var (id, num, title) in new[] { (blank, $"FRM-VAL-{tag}", "Fuel Pump Validation - Rev C"), (pdf, $"PDF-{tag}", "Final inspection scan") })
        {
            var before = await c.GetElAsync($"/api/imported-files/{id}");
            Assert.True(before.GetProperty("canEdit").GetBoolean());
            var put = await c.PutAsync($"/api/sequfloh/records/file/{id}/number", new { number = num, title });
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
            var after = await c.GetElAsync($"/api/imported-files/{id}");
            Assert.Equal(num, after.GetProperty("number").GetString());
            Assert.Equal(title, after.GetProperty("title").GetString());
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.EntityId == id && e.Action == "Changed number");
            Assert.Contains(before.GetProperty("number").GetString()!, a.Changes);
            Assert.Contains(num, a.Changes);
        }
        Assert.True((await c.GetElAsync($"/api/imported-files/{blank}")).GetProperty("isBlankForm").GetBoolean()); // still a protected blank
    }

    [Fact]
    public async Task A_salt_spray_test_work_order_gets_only_the_confirmed_test_form()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Assert.True((await admin.PutAsync("/api/sequfloh/validation-forms/tests/salt-spray", new { blankFormId = (Guid?)null })).IsSuccessStatusCode);
        var blank = await UploadAsync(c, site, $"Salt Spray Test Report {tag}.csv", Encoding.UTF8.GetBytes("Hours,Result\n96,\n"), "blankForm");

        var row = (await c.GetElAsync("/api/sequfloh/validation-forms")).GetProperty("tests").EnumerateArray().Single();
        Assert.Equal("Salt Spray Test", row.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("blankFormId").ValueKind); // suggestion only, never used
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("suggestedId").ValueKind);

        var r1 = await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site })).JsonAsync();
        Assert.Contains("No form is confirmed for Salt Spray Test", r1.GetProperty("validationForm").GetProperty("message").GetString());
        Assert.False(r1.GetProperty("validationForm").TryGetProperty("fileId", out _));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsync("/api/sequfloh/validation-forms/tests/salt-spray", new { blankFormId = blank })).StatusCode);
        Assert.True((await admin.PutAsync("/api/sequfloh/validation-forms/tests/salt-spray", new { blankFormId = blank })).IsSuccessStatusCode);
        Assert.Equal(blank, (await c.GetElAsync("/api/sequfloh/validation-forms")).GetProperty("tests")[0].GetProperty("blankFormId").GetGuid());

        var r2 = await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site, formNumber = "SS-FORM-" + Guid.NewGuid().ToString("N")[..8] })).JsonAsync();
        var fileId = Guid.Parse(r2.GetProperty("validationForm").GetProperty("fileId").GetString()!);
        var file = await c.GetElAsync($"/api/imported-files/{fileId}");
        Assert.False(file.GetProperty("isBlankForm").GetBoolean());
        Assert.Equal(blank, file.GetProperty("blankForm").GetProperty("id").GetGuid());
        var rec = (await c.GetElAsync($"{T}/{r2.Id()}")).GetProperty("record");
        Assert.Equal(fileId.ToString(), rec.GetProperty("details").GetProperty("validationForm").GetProperty("fileId").GetString());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Description.Contains("Form for Salt Spray Test changed from none to")));
        }
        Assert.True((await admin.PutAsync("/api/sequfloh/validation-forms/tests/salt-spray", new { blankFormId = (Guid?)null })).IsSuccessStatusCode);
    }
}
