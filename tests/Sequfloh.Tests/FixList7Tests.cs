using System.Net;
using System.Text;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>A validation or Salt Spray Test form that could not be started at create can be attached later. Made-up data only.</summary>
[Collection("api")]
public sealed class FixList7Tests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string Setting = "/api/sequfloh/validation-forms/tests/salt-spray";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohCancelTask, Permissions.SequflohImportFiles, Permissions.SequflohViewImportedFiles];

    [Fact]
    public async Task A_form_can_be_attached_later_with_a_free_number_only_while_the_work_order_is_open()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Hours,Result\n96,\n")), "file", $"Salt Spray Attach {tag}.csv" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var blank = (await up.JsonAsync()).Id();
        try
        {
            Assert.True((await admin.PutAsync(Setting, new { blankFormId = blank })).IsSuccessStatusCode);
            var a = await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site, number = $"SS-{tag}-1" })).JsonAsync();
            Assert.True(a.GetProperty("validationForm").TryGetProperty("fileId", out _));
            var first = a.GetProperty("validationForm").GetProperty("fileNumber").GetString()!;
            Assert.EndsWith("-1", first);
            Assert.Contains(first, await (await c.Http.GetAsync("/api/imported-files?kind=filled&family=salt-spray&pageSize=200")).Content.ReadAsStringAsync());
            Assert.DoesNotContain(first, await (await c.Http.GetAsync("/api/imported-files?kind=filled&family=none&pageSize=200")).Content.ReadAsStringAsync());
            Assert.False((await c.GetElAsync($"{T}/{a.Id()}")).GetProperty("canAttachForm").GetBoolean());

            Assert.True((await admin.PutAsync(Setting, new { blankFormId = (Guid?)null })).IsSuccessStatusCode);
            var b = (await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site, number = $"SS-{tag}-2" })).JsonAsync()).Id();
            var cc = (await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site, number = $"SS-{tag}-3" })).JsonAsync()).Id();
            Assert.True((await admin.PutAsync(Setting, new { blankFormId = blank })).IsSuccessStatusCode);
            Assert.True((await c.GetElAsync($"{T}/{b}")).GetProperty("canAttachForm").GetBoolean());

            var dup = await c.PostAsync($"{T}/{b}/validation-form", new { number = first });
            Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
            Assert.Contains("could not be started", await dup.Content.ReadAsStringAsync());

            var ok = await c.PostAsync($"{T}/{b}/validation-form", new { number = $"SS-{tag}-2" });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var rec = await c.GetElAsync($"{T}/{b}");
            Assert.False(rec.GetProperty("canAttachForm").GetBoolean());
            Assert.Equal($"SS-{tag}-2", rec.GetProperty("record").GetProperty("details").GetProperty("validationForm").GetProperty("fileNumber").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{b}/validation-form", new { number = $"SS-{tag}-9" })).StatusCode);

            var next = await c.PostAsync($"{T}/{cc}/validation-form", new { number = "" });
            Assert.True(next.IsSuccessStatusCode, await next.Content.ReadAsStringAsync());
            Assert.Equal(first[..^1] + "2", (await c.GetElAsync($"{T}/{cc}")).GetProperty("record").GetProperty("details").GetProperty("validationForm").GetProperty("fileNumber").GetString());

            var ver = (await c.GetElAsync($"{T}/{cc}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
            var cancel = await c.PostAsync($"{T}/{cc}/actions/cancel", new { operationId = Guid.NewGuid(), stateVersion = ver, reason = "Made by mistake" });
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{cc}/validation-form", new { number = $"SS-{tag}-3" })).StatusCode);
        }
        finally { await admin.PutAsync(Setting, new { blankFormId = (Guid?)null }); }
    }

    [Fact]
    public async Task Clocked_create_time_is_saved_as_one_person_time_on_the_work_order()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var started = DateTimeOffset.UtcNow.AddMinutes(-7);
        var wo = await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, number = $"CT-{tag}", createClockStartedAt = started })).JsonAsync();
        var json = await (await c.Http.GetAsync($"{T}/{wo.Id()}/crew")).Content.ReadAsStringAsync();
        Assert.Contains("Creating the work order", json);
        var none = await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, number = $"CT-{tag}-2" })).JsonAsync();
        Assert.DoesNotContain("Creating the work order", await (await c.Http.GetAsync($"{T}/{none.Id()}/crew")).Content.ReadAsStringAsync());
        var old = await c.PostAsync(T, new { type = "Inspection", siteId = site, number = $"CT-{tag}-3", createClockStartedAt = DateTimeOffset.UtcNow.AddHours(-30) });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
    }
}
