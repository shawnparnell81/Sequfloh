using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class InspectionTemplateTests(TestApp app)
{
    [Fact]
    public async Task The_standard_inspection_templates_are_seeded_once_with_the_common_steps()
    {
        using (var scope = app.Services.CreateScope())
            await SequflohInspectionTemplates.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>()); // running again adds nothing
        var site = await app.CreateSiteAsync();
        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var list = (await user.GetElAsync("/api/sequfloh/instruction-templates")).EnumerateArray().ToList();
        Assert.Equal(8, SequflohInspectionTemplates.Names.Count);
        foreach (var name in SequflohInspectionTemplates.Names)
        {
            var t = Assert.Single(list, x => x.GetProperty("name").GetString() == name);
            var text = t.GetProperty("blocks").GetRawText();
            foreach (var s in new[] { "Purpose", "Required tools and gauges", "Record results", "Segregate counts", "Next step", "repair work order", "NetSuite" }) Assert.Contains(s, text);
            Assert.DoesNotContain("signer", text);
            Assert.DoesNotContain("serial", text, StringComparison.OrdinalIgnoreCase);
        }
        var aql = list.Single(x => x.GetProperty("name").GetString() == "Inspection - AQL Sample Result").GetProperty("blocks").GetRawText();
        Assert.Contains("accept and reject numbers", aql);
        var recall = list.Single(x => x.GetProperty("name").GetString() == "Inspection - Recall / Containment Sort").GetProperty("blocks").GetRawText();
        Assert.Contains("Inspect 100%", recall);
    }
}
