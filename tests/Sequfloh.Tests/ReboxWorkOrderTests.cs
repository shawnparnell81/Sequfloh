using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Rebox work orders, WO-RBX-{family}{yy}-001. Made-up data only.</summary>
[Collection("api")]
public sealed class ReboxWorkOrderTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    static object Part(string pn, Guid site, Guid? typeId) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId, attributes = (object?)null,
    };

    [Fact]
    public async Task Rebox_work_orders_number_from_the_part_family_and_list_under_rebox()
    {
        Assert.Equal(8, (int)TrackingType.Rebox);
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"R{tag}", label = $"Rebox family {tag}", description = (string?)null })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = $"csa{tag}" })).IsSuccessStatusCode);
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("RB-" + tag, site, fam))).JsonAsync()).Id();
        var loose = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("RL-" + tag, site, null))).JsonAsync()).Id();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask], site);
        var yy = (DateTime.Now.Year % 100).ToString("00");
        Assert.Equal($"WO-RBX-CSA{tag}{yy}-001", (await c.GetElAsync($"{T}/next-number?type=Rebox&partId={part}&siteId={site}")).GetProperty("number").GetString());
        async Task<System.Text.Json.JsonElement> Create(object body) { var id = (await (await c.PostOkAsync(T, body)).JsonAsync()).Id(); return (await c.GetElAsync($"{T}/{id}")).GetProperty("record"); }
        var r1 = await Create(new { type = "Rebox", siteId = site, partId = part, number = (string?)null });
        Assert.Equal($"WO-RBX-CSA{tag}{yy}-001", r1.GetProperty("number").GetString());
        Assert.Equal("Rebox", r1.GetProperty("type").GetString());
        Assert.Equal($"WO-RBX-CSA{tag}{yy}-002", (await Create(new { type = "Rebox", siteId = site, partId = part, number = (string?)null })).GetProperty("number").GetString());
        // no family code: typed number
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await c.GetElAsync($"{T}/next-number?type=Rebox&partId={loose}&siteId={site}")).GetProperty("number").ValueKind);
        Assert.Equal($"RB-MINE-{tag}", (await Create(new { type = "Rebox", siteId = site, partId = loose, number = $"RB-MINE-{tag}" })).GetProperty("number").GetString());
        // the Rebox folder lists them
        var list = (await c.GetElAsync($"{T}?folder=rebox&siteId={site}&pageSize=50")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("type").GetString()).ToList();
        Assert.Equal(3, list.Count);
        Assert.All(list, t => Assert.Equal("Rebox", t));
        // default code in Tracking settings
        var (_, settingsAdmin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        Assert.Equal("RBX", (await settingsAdmin.GetElAsync("/api/sequfloh/settings")).GetProperty("tracking").GetProperty("reboxNumberCode").GetString());
    }
}
