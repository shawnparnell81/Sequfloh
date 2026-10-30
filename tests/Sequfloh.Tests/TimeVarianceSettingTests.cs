using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>The per-work-order Track time variance setting. Made-up data only.</summary>
[Collection("api")]
public sealed class TimeVarianceSettingTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Lead = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
        Permissions.SequflohViewReports, Permissions.SequflohViewLiveExecution, Permissions.SequflohManageTimeVariance];
    static readonly string[] Plain = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewLiveExecution];

    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
    static async Task<HttpResponseMessage> Complete(ApiClient c, Guid id, string? reason = null) =>
        await c.PostAsync($"{T}/{id}/actions/complete", new { operationId = Guid.NewGuid(), stateVersion = await Ver(c, id), varianceReason = reason });

    // a started inspection with a 1 to 2 minute benchmark that has been running for 30 minutes
    async Task<(Guid Id, ApiClient Lead, ApiClient Plain)> LateAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, lead) = await app.CreateUserAsync(Lead, site);
        var (_, plain) = await app.CreateUserAsync(Plain, site);
        var id = (await (await lead.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 1, maxBenchmarkMinutes = 2 })).JsonAsync()).Id();
        await lead.StartAsync(id);
        using var s = app.Services.CreateScope();
        await s.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sqf.sequfloh_tracking_records SET \"CurrentStateStartedAt\" = \"CurrentStateStartedAt\" - make_interval(mins => 30) WHERE \"Id\" = {id}");
        return (id, lead, plain);
    }

    [Fact]
    public async Task Off_by_default_completes_without_a_reason_and_hides_variance_but_keeps_the_stored_value()
    {
        var (id, lead, _) = await LateAsync();
        Assert.False((await lead.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("trackTimeVariance").GetBoolean());
        var done = await Complete(lead, id);
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        using (var s = app.Services.CreateScope())
        {
            var rec = await s.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTrackingRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.InRange(rec.VarianceMinutes!.Value, 27m, 29m); // still worked out and stored
        }
        var no = (await lead.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number");
        var row = (await lead.GetElAsync($"{T}?folder=completed&q={no}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("varianceMinutes").ValueKind);
        var pdf = Encoding.Latin1.GetString(await (await lead.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("Variance reason", pdf); Assert.DoesNotContain("Variance \\(minutes", pdf);
        var rep = await (await lead.GetAsync($"/api/sequfloh/reports/time-variance")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(no!, rep);
    }

    [Fact]
    public async Task On_asks_for_the_reason_shows_and_prints_variance_and_the_change_is_permission_gated_and_audited()
    {
        var (id, lead, plain) = await LateAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsync($"{T}/{id}/variance-setting", new { trackTimeVariance = true })).StatusCode);
        await lead.PutOkAsync($"{T}/{id}/variance-setting", new { trackTimeVariance = true });
        var refused = await Complete(lead, id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Enter the variance reason", await refused.Content.ReadAsStringAsync());
        var ok = await Complete(lead, id, "Made-up fixture change");
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        var no = (await lead.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number");
        var row = (await lead.GetElAsync($"{T}?folder=completed&q={no}")).GetProperty("items").EnumerateArray().Single();
        Assert.InRange(row.GetProperty("varianceMinutes").GetDecimal(), 27m, 29m);
        var pdf = Encoding.Latin1.GetString(await (await lead.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Variance reason", pdf); Assert.Contains("Made-up fixture change", pdf);
        // closed work orders keep their setting
        Assert.Equal(HttpStatusCode.Conflict, (await lead.PutAsync($"{T}/{id}/variance-setting", new { trackTimeVariance = false })).StatusCode);
        using var s = app.Services.CreateScope();
        var audit = await s.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.Where(a => a.EntityId == id && a.Action == "TimeVarianceSetting").Select(a => a.Description).ToListAsync();
        Assert.Contains("set to Yes", Assert.Single(audit));
    }
}
