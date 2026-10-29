using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Test work orders: the product family test template, the drawing spec once and Part 1, Part 2, ... results. Made-up data only.</summary>
[Collection("api")]
public sealed class TestWorkInstructionTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohViewDrawings];

    sealed record Setup(Guid Site, ApiClient C, Guid Pump, Guid Csa, Guid PumpPart, Guid CsaPart, Guid Drawing);

    async Task<Setup> SetupAsync(string[]? extra = null, bool drawing = true)
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Tech, .. extra ?? []], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fam = await db.SequflohReferenceValues.Where(v => v.ListKey == "product-families").ToDictionaryAsync(v => v.Code, v => v);
        var pump = fam["FUEL_PUMP"]; var csa = fam.Values.First(v => v.Label.Contains("CSA"));
        var pp = new Part { PartNumber = "TFP" + tag, Name = "Example pump " + tag, SiteId = site, Status = MasterStatus.Active, TypeId = pump.Id, ProductFamily = pump.Label };
        var cp = new Part { PartNumber = "TCS" + tag, Name = "Example strut " + tag, SiteId = site, Status = MasterStatus.Active, TypeId = csa.Id, ProductFamily = csa.Label };
        db.Parts.AddRange(pp, cp);
        var folder = new DrawingFolder { Name = "Test drawings " + tag, SiteId = site };
        db.DrawingFolders.Add(folder); await db.SaveChangesAsync();
        var d = new Drawing { FolderId = folder.Id, Title = "Pump drawing " + tag, DrawingNumber = "DWG-" + tag, SiteId = site, CurrentRevision = "B" };
        db.Drawings.Add(d);
        if (drawing) db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = d.Id, PartId = pp.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new(site, c, pump.Id, csa.Id, pp.Id, cp.Id, d.Id);
    }

    static string Num() => "TST-" + Guid.NewGuid().ToString("N")[..8];

    async Task<Guid> CreateAsync(Setup s, Guid family, Guid part, ApiClient? c = null) =>
        (await (await (c ?? s.C).PostOkAsync(T, new { type = "Test", number = Num(), siteId = s.Site, familyId = family, partId = part })).JsonAsync()).Id();

    async Task<List<(string Action, string Description, string? Reason)>> AuditsAsync(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id).Select(a => new { a.Action, a.Description, a.Reason }).ToListAsync()).Select(a => (a.Action, a.Description ?? "", a.Reason)).ToList();
    }

    static object Vals(params (string K, string? V)[] v) => v.Select(x => new { key = x.K, value = x.V }).ToList();

    [Fact]
    public async Task A_test_order_needs_the_part_and_the_product_family()
    {
        var s = await SetupAsync();
        var noPart = await s.C.PostAsync(T, new { type = "Test", number = Num(), siteId = s.Site, familyId = s.Pump });
        Assert.Equal(HttpStatusCode.BadRequest, noPart.StatusCode);
        Assert.Contains("part number", await noPart.Content.ReadAsStringAsync());
        // no family chosen: the part's own product family is used
        var inferred = (await (await s.C.PostOkAsync(T, new { type = "Test", number = Num(), siteId = s.Site, partId = s.PumpPart })).JsonAsync()).Id();
        Assert.Equal("fuel-pump", (await s.C.GetElAsync($"{T}/{inferred}/test")).GetProperty("template").Str("key"));
        Guid loose;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Part { PartNumber = "TNF" + Guid.NewGuid().ToString("N")[..6], Name = "Example part with no family", SiteId = s.Site, Status = MasterStatus.Active };
            db.Parts.Add(p); await db.SaveChangesAsync(); loose = p.Id;
        }
        var noFam = await s.C.PostAsync(T, new { type = "Test", number = Num(), siteId = s.Site, partId = loose });
        Assert.Equal(HttpStatusCode.BadRequest, noFam.StatusCode);
        Assert.Contains("product family", await noFam.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_CSA_test_order_shows_no_fuel_pump_fields()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s, s.Csa, s.CsaPart);
        var t = await s.C.GetElAsync($"{T}/{id}/test");
        Assert.Equal("order-only", t.GetProperty("template").Str("key"));
        Assert.Equal(0, t.GetProperty("template").GetProperty("fields").GetArrayLength());
        Assert.DoesNotContain("Flow rate", t.GetRawText());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.C.PostAsync($"{T}/{id}/test/parts", new { values = Array.Empty<object>() })).StatusCode);
        var print = Encoding.Latin1.GetString(await (await s.C.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("Part 1 test results", print);
        Assert.DoesNotContain("Float arm", print);
    }

    [Fact]
    public async Task The_exact_part_drawing_is_attached_and_a_part_without_one_can_have_one_picked_by_hand()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s, s.Pump, s.PumpPart);
        Assert.Contains((await s.C.GetElAsync($"/api/drawings/for-record/{id}")).EnumerateArray(), d => d.Id() == s.Drawing);
        var bare = await SetupAsync(drawing: false);
        var id2 = await CreateAsync(bare, bare.Pump, bare.PumpPart);
        Assert.Empty((await bare.C.GetElAsync($"/api/drawings/for-record/{id2}")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await bare.C.PostAsync($"{T}/{id2}/drawings", new { drawingId = bare.Drawing })).StatusCode);
        Assert.Contains((await bare.C.GetElAsync($"/api/drawings/for-record/{id2}")).EnumerateArray(), d => d.Id() == bare.Drawing);
        Assert.Contains(await AuditsAsync(id2), a => a.Action == "DrawingAttached" && a.Description.Contains("attached by hand"));
    }

    [Fact]
    public async Task Fuel_pump_spec_once_and_each_part_gets_its_own_numbered_results_on_the_print_and_job_report()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s, s.Pump, s.PumpPart);
        var t = await s.C.GetElAsync($"{T}/{id}/test");
        Assert.Equal("fuel-pump", t.GetProperty("template").Str("key"));
        Assert.Equal(new[] { "psi", "psi", "L/h", "ohms", "ohms", "A" }, t.GetProperty("spec").EnumerateArray().Select(x => x.Str("unit")).ToArray());
        await s.C.PutAsync($"{T}/{id}/test/spec", new { values = new object[] { new { key = "testPressure", value = "60", unit = "psi" }, new { key = "flowRate", value = "100", unit = "L/h" },
            new { key = "floatFull", value = "250", unit = "ohms" }, new { key = "floatEmpty", value = "40", unit = "ohms" }, new { key = "amps", value = "6.5", unit = "A" } } });
        var p1 = (await (await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Vals(("testPressure", "59"), ("flowRate", "95"), ("floatFull", "248"), ("floatEmpty", "41"), ("amps", "6.4")), note = "Made-up first pump" })).JsonAsync());
        Assert.Equal(1, p1.GetProperty("partIndex").GetInt32());
        var p2 = (await (await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Vals(("testPressure", "61"), ("flowRate", "102")) })).JsonAsync());
        Assert.Equal(2, p2.GetProperty("partIndex").GetInt32());
        var bad = await s.C.PostAsync($"{T}/{id}/test/parts", new { values = Vals(("amps", "lots")) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var audits = await AuditsAsync(id);
        Assert.Contains(audits, a => a.Description.Contains("Part 1: Actual flow rate set to 95 L/h"));
        Assert.Contains(audits, a => a.Description.Contains("Spec flow rate set to 100 L/h"));
        foreach (var url in new[] { $"{T}/{id}/print", $"{T}/{id}/job/report/pdf" })
        {
            var pdf = Encoding.Latin1.GetString(await (await s.C.GetAsync(url)).Content.ReadAsByteArrayAsync());
            Assert.Contains("Part 1 test results", pdf); Assert.Contains("Part 2 test results", pdf);
            Assert.Contains("Drawing spec", pdf);
            Assert.Contains("BLANK - not entered", pdf);
            Assert.DoesNotContain("Serial", pdf.Split("Drawing spec")[1].Split("Steps")[0]);
        }
    }

    [Fact]
    public async Task Changing_entered_values_needs_the_permission_and_a_reason_and_removed_parts_keep_their_numbers()
    {
        var s = await SetupAsync();
        var id = await CreateAsync(s, s.Pump, s.PumpPart);
        var p1 = (await (await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Vals(("flowRate", "90")) })).JsonAsync()).Id();
        // filling a blank is open to the technician; changing an entered value is not
        Assert.Equal(HttpStatusCode.NoContent, (await s.C.PutAsync($"{T}/test/parts/{p1}", new { values = Vals(("amps", "6")) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.C.PutAsync($"{T}/test/parts/{p1}", new { values = Vals(("flowRate", "95")), reason = "typo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.C.PostAsync($"{T}/test/parts/{p1}/remove", new { reason = "wrong part" })).StatusCode);

        var (_, lead) = await app.CreateUserAsync([.. Tech, Permissions.SequflohCorrectTestResults, Permissions.SequflohRemoveTestResults, Permissions.SequflohViewAuditTrail], s.Site);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PutAsync($"{T}/test/parts/{p1}", new { values = Vals(("flowRate", "95")) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lead.PutAsync($"{T}/test/parts/{p1}", new { values = Vals(("flowRate", "95")), reason = "Made-up misread gauge" })).StatusCode);
        var p2 = (await (await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Vals(("flowRate", "99")) })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.PostAsync($"{T}/test/parts/{p1}/remove", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lead.PostAsync($"{T}/test/parts/{p1}/remove", new { reason = "Made-up wrong pump" })).StatusCode);
        var p3 = (await (await s.C.PostOkAsync($"{T}/{id}/test/parts", new { values = Vals() })).JsonAsync());
        Assert.Equal(3, p3.GetProperty("partIndex").GetInt32());
        var t = await s.C.GetElAsync($"{T}/{id}/test");
        Assert.Equal(new[] { 2, 3 }, t.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("partIndex").GetInt32()).ToArray());
        Assert.Equal(1, t.GetProperty("removed")[0].GetProperty("partIndex").GetInt32());
        Assert.NotEqual(Guid.Empty, p2);
        var audits = await AuditsAsync(id);
        Assert.Contains(audits, a => a.Action == "TestPartCorrected" && a.Description.Contains("Actual flow rate changed from 90 L/h to 95 L/h") && a.Reason == "Made-up misread gauge");
        Assert.Contains(audits, a => a.Action == "TestPartRemoved" && a.Description.Contains("Part 1 test results removed") && a.Description.Contains("Made-up wrong pump"));
        var history = (await lead.GetElAsync($"{T}/{id}/history")).GetRawText();
        Assert.Contains("Part 1 test results removed", history);
    }
}
