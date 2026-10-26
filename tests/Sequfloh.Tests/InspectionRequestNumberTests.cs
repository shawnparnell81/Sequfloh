using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Tests;

/// <summary>Automatic inspection request numbers, INSP-{family}{yy}-001. Made-up data only.</summary>
[Collection("api")]
public sealed class InspectionRequestNumberTests(TestApp app)
{
    const string A = "/api/sequfloh/inspection-requests";

    static object Part(string pn, Guid site, Guid? typeId) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId, attributes = (object?)null,
    };

    [Fact]
    public async Task New_requests_number_as_INSP_family_year_and_count_and_stay_editable()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"I{tag}", label = $"Insp family {tag}", description = (string?)null })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = $"c{tag}" })).IsSuccessStatusCode);
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("IP-" + tag, site, fam))).JsonAsync()).Id();
        var loose = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("IL-" + tag, site, null))).JsonAsync()).Id();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var yy = (DateTime.Now.Year % 100).ToString("00");
        Assert.Equal($"INSP-C{tag}{yy}-001", (await c.GetElAsync($"{A}/next-number?siteId={site}&partId={part}")).GetProperty("number").GetString());
        Assert.Equal($"INSP-C{tag}{yy}-001", (await c.GetElAsync($"{A}/next-number?siteId={site}&partNumber=IP-{tag}")).GetProperty("number").GetString());
        async Task<string?> Create(object body) { var id = (await (await c.PostOkAsync(A, body)).JsonAsync()).Id(); return (await c.GetElAsync($"{A}/{id}")).GetProperty("number").GetString(); }
        Assert.Equal($"INSP-C{tag}{yy}-001", await Create(new { siteId = site, partId = part, number = (string?)null }));
        Assert.Equal($"INSP-C{tag}{yy}-002", await Create(new { siteId = site, partId = part, number = (string?)null }));
        Assert.Equal($"MINE-{tag}", await Create(new { siteId = site, partId = part, number = $"MINE-{tag}" }));
        Assert.Equal($"INSP-C{tag}{yy}-003", (await c.GetElAsync($"{A}/next-number?siteId={site}&partId={part}")).GetProperty("number").GetString());
        // no family: typed number, as before
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await c.GetElAsync($"{A}/next-number?siteId={site}&partId={loose}")).GetProperty("number").ValueKind);
    }

    [Fact]
    public void Year_rolls_over_at_local_midnight_on_January_1()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        // 03:30 UTC on Jan 1 2027 is still Dec 31 2026 in New York
        Assert.Equal("26", SequflohTrackingService.YearCode(new DateTimeOffset(2027, 1, 1, 3, 30, 0, TimeSpan.Zero), ny));
        Assert.Equal("27", SequflohTrackingService.YearCode(new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero), ny));
    }
}
