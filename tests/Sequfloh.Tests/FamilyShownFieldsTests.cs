using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>An administrator can turn on a field for a product family that does not show it anyway (for example NAPA PN). Synthetic data only.</summary>
[Collection("api")]
public sealed class FamilyShownFieldsTests(TestApp app)
{
    [Fact]
    public void A_family_setting_turns_on_a_field_without_changing_the_others()
    {
        var napaPn = PartAttributes.Find("napaPn")!; var napaUs = PartAttributes.Find("napaUs")!;
        Assert.False(PartAttributes.AppliesTo(napaPn, "AIR_SUSPENSION"));
        Assert.True(PartAttributes.AppliesTo(napaPn, "AIR_SUSPENSION", "napaPn"));
        Assert.True(PartAttributes.AppliesTo(napaUs, "AIR_SUSPENSION", "napaPn"));
        Assert.False(PartAttributes.AppliesTo(PartAttributes.Find("dmaPn")!, "AIR_SUSPENSION", "napaPn"));
        Assert.True(PartAttributes.AppliesTo(napaPn, "FUEL_PUMP"));
    }

    [Fact]
    public async Task Extra_fields_are_saved_on_the_family_audited_and_kept_on_its_parts()
    {
        var admin = await app.AdminAsync();
        var site = (await admin.GetElAsync("/api/organization/sites"))[0].Id();
        var fam = (await admin.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(v => v.GetProperty("isActive").GetBoolean() && v.Str("code") != "FUEL_PUMP");
        var url = $"/api/sequfloh/reference/values/{fam.Id()}/shown-fields";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(url, new { fields = new[] { "made-up" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync(url, new { fields = new[] { "napaPn" } })).StatusCode);
        try
        {
            var now = (await admin.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(v => v.Id() == fam.Id());
            Assert.Equal("napaPn", now.Str("shownFields"));
            var pn = "SF-" + Guid.NewGuid().ToString("N")[..6];
            object Body(Dictionary<string, string?> attrs) => new { part = new { partNumber = pn, name = "Made-up part", partType = 1, revision = "A", unitOfMeasure = "EA", siteId = site, status = 0, hasSpecialCharacteristics = false },
                execution = new { isRepairable = false, isSerialized = false, isLotControlled = false }, typeId = fam.Id(), attributes = attrs };
            var id = (await (await admin.PostOkAsync("/api/sequfloh/parts", Body(new() { ["napaPn"] = "NP-" + pn, ["napaUs"] = "NU-" + pn }))).JsonAsync()).Id();
            var attrs = (await admin.GetElAsync($"/api/sequfloh/parts/{id}")).GetProperty("part").GetProperty("attributes");
            Assert.Equal("NP-" + pn, attrs.Str("napaPn"));
            Assert.Equal("NU-" + pn, attrs.Str("napaUs"));

            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == fam.Id() && a.Action == "FamilyShownFieldsChanged").OrderByDescending(a => a.OccurredAt).FirstAsync();
            Assert.Equal($"Extra fields shown for product family {fam.Str("label")} changed from none to NAPA PN", audit.Description);
        }
        finally { await admin.PutAsync(url, new { fields = Array.Empty<string>() }); }
    }
}
