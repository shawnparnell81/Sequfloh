using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Drawing spec values typed as ranges and limits (320-360, 45 ± 5, ≥95, &lt;=6.0) are kept exactly as typed with their min and max. Made-up data only.</summary>
[Collection("api")]
public sealed class SpecRangeTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task Spec_ranges_and_limits_are_stored_as_typed_and_print_that_way()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid pump, part;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = await db.SequflohReferenceValues.FirstAsync(v => v.ListKey == "product-families" && v.Code == "FUEL_PUMP");
            var p = new Part { PartNumber = "SRP" + tag, Name = "Example pump", SiteId = site, Status = MasterStatus.Active, TypeId = f.Id, ProductFamily = f.Label };
            db.Parts.Add(p); await db.SaveChangesAsync(); pump = f.Id; part = p.Id;
        }
        var id = (await (await c.PostOkAsync(T, new { type = "Test", number = "SRT-" + tag, siteId = site, familyId = pump, partId = part })).JsonAsync()).Id();
        var bad = await c.PutAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "shutoffPressure", value = "about 340", unit = "psi" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("range", await bad.Content.ReadAsStringAsync());
        await c.PutOkAsync($"{T}/{id}/test/spec", new { values = new[] {
            new { key = "testPressure", value = "60", unit = "psi" }, new { key = "shutoffPressure", value = "320-360", unit = "psi" },
            new { key = "flowRate", value = "≥95", unit = "L/h" }, new { key = "amps", value = "<=6.0", unit = "A" }, new { key = "floatFull", value = "250 ± 5", unit = "ohms" }, new { key = "floatEmpty", value = ">40", unit = "ohms" } } });
        await c.PostOkAsync($"{T}/{id}/test/fields", new { label = "Leak rate", unit = "cc/min", value = "≤6.0" });
        var t = await c.GetElAsync($"{T}/{id}/test");
        JsonElement S(string k) => t.GetProperty("spec").EnumerateArray().Single(x => x.Str("key") == k);
        Assert.Equal("60", S("testPressure").Str("value")); Assert.Equal(JsonValueKind.Null, S("testPressure").GetProperty("min").ValueKind);
        Assert.Equal("320-360", S("shutoffPressure").Str("value")); Assert.Equal(320, S("shutoffPressure").GetProperty("min").GetDecimal()); Assert.Equal(360, S("shutoffPressure").GetProperty("max").GetDecimal());
        Assert.Equal("≥95", S("flowRate").Str("value")); Assert.Equal(95, S("flowRate").GetProperty("min").GetDecimal()); Assert.Equal(JsonValueKind.Null, S("flowRate").GetProperty("max").ValueKind);
        Assert.Equal("<=6.0", S("amps").Str("value")); Assert.Equal(6.0m, S("amps").GetProperty("max").GetDecimal());
        Assert.Equal(245, S("floatFull").GetProperty("min").GetDecimal()); Assert.Equal(255, S("floatFull").GetProperty("max").GetDecimal());
        Assert.True(S("floatEmpty").GetProperty("strict").GetBoolean());
        Assert.Equal("≤6.0", t.GetProperty("spec").EnumerateArray().Last().Str("value"));
        // actuals stay single numbers
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = "330-340" } } })).StatusCode);
        await c.PostOkAsync($"{T}/{id}/test/parts", new { values = new[] { new { key = "shutoffPressure", value = "370" } } });
        var print = Encoding.UTF8.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        var latin = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.True(latin.Contains("320-360 psi") || print.Contains("320-360 psi"));
        // re-saving with the same text keeps it; changing it is a correction with a reason
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsync($"{T}/{id}/test/spec", new { values = new[] { new { key = "shutoffPressure", value = "330-360", unit = "psi" } }, reason = "rev B" })).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.AuditEntries.AsNoTracking().Where(x => x.EntityId == id && x.Action == "TestSpecSet").Select(x => x.Description).ToListAsync();
            Assert.Contains(a, x => x!.Contains("set to 320-360 psi")); Assert.Contains(a, x => x!.Contains("set to ≥95 L/h"));
        }
    }
}
