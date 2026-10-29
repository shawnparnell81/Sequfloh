using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Linking a product family to a test template: the exact "Fuel Pump" name on upgrade, and the administrator's choice. Made-up data only.</summary>
[Collection("api")]
public sealed class FamilyTestTemplateTests(TestApp app)
{
    [Fact]
    public async Task The_upgrade_links_only_a_family_named_exactly_Fuel_Pump_and_changes_nothing_else_on_it()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        // the seeded family is moved aside so the made-up one stands in for an owner's own family
        var seeded = await db.SequflohReferenceValues.Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.TestTemplate != null).ToListAsync();
        var exact = new SequflohReferenceValue { ListKey = SequflohCatalog.ProductFamilies, Code = "OWN_FP_" + tag, Label = "  fuel PUMP ", NumberCode = "FPX" };
        var near = new SequflohReferenceValue { ListKey = SequflohCatalog.ProductFamilies, Code = "OWN_FPS_" + tag, Label = "Fuel Pumps " + tag };
        db.SequflohReferenceValues.AddRange(exact, near);
        await db.SaveChangesAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(SequflohTestTemplates.LinkFuelPumpSql);
            var e = await db.SequflohReferenceValues.AsNoTracking().SingleAsync(v => v.Id == exact.Id);
            var n = await db.SequflohReferenceValues.AsNoTracking().SingleAsync(v => v.Id == near.Id);
            Assert.Equal("fuel-pump", e.TestTemplate);
            Assert.Equal(("  fuel PUMP ", "OWN_FP_" + tag, "FPX"), (e.Label, e.Code, e.NumberCode));
            Assert.Null(n.TestTemplate);
            Assert.All(seeded, s => Assert.Equal("fuel-pump", db.SequflohReferenceValues.AsNoTracking().Single(v => v.Id == s.Id).TestTemplate));
        }
        finally
        {
            await db.SequflohReferenceValues.Where(v => v.Id == exact.Id || v.Id == near.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task An_administrator_picks_the_test_template_for_a_family_and_it_is_audited()
    {
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageDropdowns]);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"P{tag}", label = $"Pump family {tag}", description = (string?)null })).JsonAsync()).Id();
        Assert.Contains((await admin.GetElAsync("/api/sequfloh/test-templates")).EnumerateArray(), t => t.Str("key") == "fuel-pump");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsync($"/api/sequfloh/reference/values/{fam}/test-template", new { key = "fuel-pump" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/test-template", new { key = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/test-template", new { key = "fuel-pump" })).StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("fuel-pump", (await db.SequflohReferenceValues.AsNoTracking().SingleAsync(v => v.Id == fam)).TestTemplate);
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == fam).Select(a => a.Description).ToListAsync(),
            d => d!.Contains($"Test template of product family Pump family {tag} changed from none to Fuel pump test"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/test-template", new { key = (string?)null })).StatusCode);
        Assert.Null((await db.SequflohReferenceValues.AsNoTracking().SingleAsync(v => v.Id == fam)).TestTemplate);
    }
}
