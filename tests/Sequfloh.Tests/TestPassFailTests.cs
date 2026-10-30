using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Pass / Fail per part on Test work orders: automatic from the spec limits (same rule as the Outside spec highlight), or set by hand. Made-up data only.</summary>
[Collection("api")]
public sealed class TestPassFailTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    async Task<(Guid Id, ApiClient C, ApiClient Lead)> OrderAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports], site);
        var (_, lead) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewReports, Permissions.SequflohCorrectTestResults], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid fam, part;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.SequflohReferenceValues.FirstAsync(v => v.ListKey == "product-families" && v.Code == "FUEL_PUMP");
            var p = new Part { PartNumber = "PF" + tag, Name = "Example pump", SiteId = site, Status = MasterStatus.Active, TypeId = f.Id, ProductFamily = f.Label };
            db.Parts.Add(p); await db.SaveChangesAsync(); fam = f.Id; part = p.Id;
        }
        var id = (await (await c.PostOkAsync(T, new { type = "Test", number = "PFT-" + tag, siteId = site, familyId = fam, partId = part })).JsonAsync()).Id();
        await c.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "testPressure", value = "60", unit = "psi" },
            new { key = "shutoffPressure", value = "320-360", unit = "psi" }, new { key = "flowRate", value = "≥95", unit = "L/h" } } });
        return (id, c, lead);
    }

    static async Task<JsonElement> Part(ApiClient c, Guid id, int n) => (await c.GetElAsync($"{T}/{id}/test")).GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("partIndex").GetInt32() == n);
    static string? R(JsonElement p, string k) => p.GetProperty(k).ValueKind == JsonValueKind.Null ? null : p.GetProperty(k).GetString();
    static async Task<List<string?>> Audit(TestApp app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking().Where(x => x.EntityId == id && x.Action.StartsWith("TestPartResult")).Select(x => x.Description).ToListAsync();
    }

    [Fact]
    public async Task Auto_pass_fail_and_blank_with_an_overall_result_and_both_prints()
    {
        var (id, c, _) = await OrderAsync();
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = "340" }, new { key = "flowRate", value = "95" } } });
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = "370" }, new { key = "flowRate", value = "99" } } });
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "testPressure", value = "61" } } }); // single-number spec only: nothing to evaluate
        Assert.Equal("Pass", R(await Part(c, id, 1), "result"));
        Assert.Equal("Fail", R(await Part(c, id, 2), "result"));
        Assert.Null(R(await Part(c, id, 3), "result")); Assert.Null(R(await Part(c, id, 3), "autoResult"));
        Assert.Equal("Fail", (await c.GetElAsync($"{T}/{id}/test")).Str("overallResult"));
        var full = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Part 1", full); Assert.Contains("PASS", full); Assert.Contains("FAIL", full); Assert.Contains("Overall test result: FAIL", full); // compact test results on the job report
        var one = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print/work-order")).Content.ReadAsByteArrayAsync());
        Assert.Contains("PASS", one); Assert.Contains("FAIL", one);
    }

    [Fact]
    public async Task Manual_set_override_needs_a_reason_changes_need_the_correct_permission_and_all_are_audited()
    {
        var (id, c, lead) = await OrderAsync();
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "testPressure", value = "60" } } });
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = "370" } } });
        var p1 = (await Part(c, id, 1)).GetProperty("id").GetGuid(); var p2 = (await Part(c, id, 2)).GetProperty("id").GetGuid();
        // no limit: the tech sets it, no reason needed
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync($"{T}/test/parts/{p1}/result", new { result = "Pass" })).StatusCode);
        var a = await Part(c, id, 1);
        Assert.Equal("Pass", R(a, "result")); Assert.Equal("Pass", R(a, "manualResult")); Assert.NotNull(R(a, "manualBy"));
        // override of an automatic Fail needs a reason
        var bad = await c.PutAsync($"{T}/test/parts/{p2}/result", new { result = "Pass" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("reason", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync($"{T}/test/parts/{p2}/result", new { result = "Pass", reason = "Made-up gauge was out of calibration" })).StatusCode);
        var b = await Part(c, id, 2);
        Assert.Equal("Pass", R(b, "result")); Assert.Equal("Fail", R(b, "autoResult")); Assert.Equal("Made-up gauge was out of calibration", R(b, "manualReason"));
        Assert.Equal("Pass", (await c.GetElAsync($"{T}/{id}/test")).Str("overallResult"));
        // changing a set result needs the correct test results permission
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsync($"{T}/test/parts/{p1}/result", new { result = "Fail" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lead.PutAsync($"{T}/test/parts/{p1}/result", new { result = "Fail" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lead.PutAsync($"{T}/test/parts/{p2}/result", new { result = "Auto" })).StatusCode);
        Assert.Equal("Fail", R(await Part(c, id, 2), "result")); Assert.Null(R(await Part(c, id, 2), "manualResult"));
        var log = await Audit(app, id);
        Assert.Contains(log, x => x!.Contains("Part 1: result set to Pass by"));
        Assert.Contains(log, x => x!.Contains("Part 2: result overridden to Pass (automatic result Fail) by") && x.Contains("Reason: Made-up gauge was out of calibration"));
        Assert.Contains(log, x => x!.Contains("Part 1: result changed from Pass to Fail by"));
        Assert.Contains(log, x => x!.Contains("Part 2: result set back to automatic (Fail), was Pass by"));
        var full = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Overall test result: FAIL", full);
    }
}
