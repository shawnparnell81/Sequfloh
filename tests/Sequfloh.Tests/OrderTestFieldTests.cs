using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Shutoff pressure on the fuel pump template, and spec fields added on one Test order. Made-up data only.</summary>
[Collection("api")]
public sealed class OrderTestFieldTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports];

    async Task<(Guid Site, ApiClient C, Guid Pump, Guid PumpPart, Guid Csa, Guid CsaPart)> SetupAsync(params string[] extra)
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Tech, .. extra], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fam = await db.SequflohReferenceValues.Where(v => v.ListKey == "product-families").ToListAsync();
        var pump = fam.First(v => v.Code == "FUEL_PUMP"); var csa = fam.First(v => v.Label.Contains("CSA"));
        var pp = new Part { PartNumber = "OFP" + tag, Name = "Example pump " + tag, SiteId = site, Status = MasterStatus.Active, TypeId = pump.Id, ProductFamily = pump.Label };
        var cp = new Part { PartNumber = "OCS" + tag, Name = "Example strut " + tag, SiteId = site, Status = MasterStatus.Active, TypeId = csa.Id, ProductFamily = csa.Label };
        db.Parts.AddRange(pp, cp); await db.SaveChangesAsync();
        return (site, c, pump.Id, pp.Id, csa.Id, cp.Id);
    }

    static async Task<Guid> CreateAsync(ApiClient c, Guid site, Guid fam, Guid part) =>
        (await (await c.PostOkAsync(T, new { type = "Test", number = "OTF-" + Guid.NewGuid().ToString("N")[..8], siteId = site, familyId = fam, partId = part })).JsonAsync()).Id();

    async Task<List<string>> AuditsAsync(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id).Select(a => a.Action + "|" + a.Description).ToListAsync();
    }

    static List<string> Keys(JsonElement t) => [.. t.GetProperty("template").GetProperty("fields").EnumerateArray().Select(f => f.Str("key")!)];

    [Fact]
    public async Task Fuel_pump_orders_have_shutoff_pressure_right_after_test_pressure_including_existing_ones()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s.C, s.Site, s.Pump, s.PumpPart);
        await s.C.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "testPressure", value = "60", unit = "psi" } } });
        await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "testPressure", value = "59" } } });
        var t = await s.C.GetElAsync($"{T}/{id}/test");
        Assert.Equal(["testPressure", "shutoffPressure", "flowRate", "floatFull", "floatEmpty", "amps"], Keys(t));
        var shut = t.GetProperty("spec").EnumerateArray().Single(x => x.Str("key") == "shutoffPressure");
        Assert.Equal("Shutoff pressure", shut.Str("label")); Assert.Equal("psi", shut.Str("unit")); Assert.Equal("", shut.Str("value"));
        var part = t.GetProperty("parts")[0].GetProperty("values").EnumerateArray().ToList();
        Assert.Equal("59", part.Single(v => v.Str("key") == "testPressure").Str("value"));
        Assert.Equal("", part.Single(v => v.Str("key") == "shutoffPressure").Str("value"));
    }

    [Fact]
    public async Task A_user_adds_a_spec_field_to_one_order_and_it_shows_in_every_part_and_the_printout()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s.C, s.Site, s.Csa, s.CsaPart);
        var other = await CreateAsync(s.C, s.Site, s.Csa, s.CsaPart);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.C.PostAsync($"{T}/{id}/test/parts", new { values = Array.Empty<object>() })).StatusCode);
        var t = await (await s.C.PostOkAsync($"{T}/{id}/test/fields", new { label = "Extended length", unit = "in", value = "12.5" })).JsonAsync();
        var key = Keys(t).Single();
        Assert.StartsWith("u-", key);
        Assert.Equal(HttpStatusCode.Conflict, (await s.C.PostAsync($"{T}/{id}/test/fields", new { label = "extended length", unit = "in" })).StatusCode);
        await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key, value = "12.4" } } });
        await s.C.PostOkAsync($"{T}/{id}/test/fields", new { label = "Compressed length", unit = "in" });
        await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Array.Empty<object>() });
        t = await s.C.GetElAsync($"{T}/{id}/test");
        Assert.Equal(2, Keys(t).Count);
        Assert.All(t.GetProperty("parts").EnumerateArray(), p => Assert.Equal(2, p.GetProperty("values").GetArrayLength()));
        Assert.Equal("12.5", t.GetProperty("spec")[0].Str("value"));
        // per order only: the other order and the family template are unchanged
        Assert.Empty(Keys(await s.C.GetElAsync($"{T}/{other}/test")));
        var print = Encoding.Latin1.GetString(await (await s.C.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Extended length", print); Assert.Contains("Compressed length", print); Assert.Contains("Part 2 test results", print);
        Assert.Contains(await AuditsAsync(id), a => a.StartsWith("TestFieldAdded|") && a.Contains("Extended length"));
    }

    [Fact]
    public async Task Renaming_or_removing_an_added_field_needs_the_correct_permission_and_a_reason_and_removal_keeps_history()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s.C, s.Site, s.Pump, s.PumpPart);
        var t = await (await s.C.PostOkAsync($"{T}/{id}/test/fields", new { label = "Leak rate", unit = "cc/min", value = "2" })).JsonAsync();
        var key = Keys(t).Last();
        await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key, value = "1.5" } } });
        Assert.Equal(HttpStatusCode.Forbidden, (await s.C.PutAsync($"{T}/{id}/test/fields/{key}", new { label = "Leak", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.C.PostAsync($"{T}/{id}/test/fields/{key}/remove", new { reason = "x" })).StatusCode);
        var (_, boss) = await app.CreateUserAsync([.. Tech, Permissions.SequflohCorrectTestResults], s.Site);
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.PutAsync($"{T}/{id}/test/fields/{key}", new { label = "Leak" })).StatusCode);
        await boss.PutOkAsync($"{T}/{id}/test/fields/{key}", new { label = "Leak-down rate", unit = "cc/min", reason = "Name on the drawing" });
        Assert.Contains("Leak-down rate", (await s.C.GetElAsync($"{T}/{id}/test")).GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.PostAsync($"{T}/{id}/test/fields/{key}/remove", new { reason = "" })).StatusCode);
        t = await (await boss.PostOkAsync($"{T}/{id}/test/fields/{key}/remove", new { reason = "Not on this revision" })).JsonAsync();
        Assert.DoesNotContain(key, Keys(t));
        var gone = t.GetProperty("addedFields").EnumerateArray().Single(f => f.Str("key") == key);
        Assert.Equal("Not on this revision", gone.Str("removeReason"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.SequflohTestPartResults.AsNoTracking().SingleAsync(x => x.TrackingRecordId == id);
            Assert.Contains(key, p.Values); // the entered value stays in history
        }
        // saving the spec or a part after removal keeps the removed field's stored value
        await s.C.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "amps", value = "6", unit = "A" } } });
        using (var scope = app.Services.CreateScope())
            Assert.Contains(key, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SequflohTestSpecs.AsNoTracking().SingleAsync(x => x.TrackingRecordId == id)).Values);
        var a = await AuditsAsync(id);
        Assert.Contains(a, x => x.StartsWith("TestFieldChanged|") && x.Contains("Name on the drawing"));
        Assert.Contains(a, x => x.StartsWith("TestFieldRemoved|") && x.Contains("Not on this revision"));
    }
}
