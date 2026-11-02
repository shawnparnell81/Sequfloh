using System.Net;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>A role for the whole of a main site covers every site; other site roles stay at their own site. Made-up data only.</summary>
[Collection("api")]
public sealed class MainSiteScopeTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Perms = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.SequflohCreateProductionTask, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static object Part(string pn, Guid site) => new
    {
        part = new { partNumber = pn, name = "Made-up part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId = (Guid?)null, attributes = (object?)null,
    };

    [Fact]
    public async Task A_main_site_role_works_at_every_site_and_other_roles_keep_to_their_own()
    {
        var admin = await app.AdminAsync();
        var main = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var plain = await app.CreateSiteAsync();
        await admin.PutOkAsync($"/api/organization/sites/{main}/main", new { isMainSite = true });
        var sites = await admin.GetElAsync("/api/organization/sites");
        Assert.True(sites.EnumerateArray().First(s => s.Id() == main).GetProperty("isMainSite").GetBoolean());

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("MU-MS-" + tag, other))).JsonAsync()).Id();
        var (_, mainUser) = await app.CreateUserAsync(Perms, main);
        var (_, siteUser) = await app.CreateUserAsync(Perms, plain);
        var (_, allUser) = await app.CreateUserAsync(Perms);

        // parts at another site: seen and edited
        Assert.Equal(HttpStatusCode.OK, (await mainUser.GetAsync($"/api/sequfloh/parts/{part}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await allUser.GetAsync($"/api/sequfloh/parts/{part}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await siteUser.GetAsync($"/api/sequfloh/parts/{part}")).StatusCode);
        Assert.True((await mainUser.PutAsync($"/api/sequfloh/parts/{part}/upc", new { upc = (string?)null })).IsSuccessStatusCode);
        Assert.False((await siteUser.PutAsync($"/api/sequfloh/parts/{part}/upc", new { upc = (string?)null })).IsSuccessStatusCode);

        // work orders at another site: created and opened
        var wo = (await (await mainUser.PostOkAsync(T, new { type = "Rebox", siteId = other, partId = part, number = "MU-MS-WO-" + tag })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, (await mainUser.GetAsync($"{T}/{wo}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await allUser.GetAsync($"{T}/{wo}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await siteUser.GetAsync($"{T}/{wo}")).StatusCode);
        Assert.False((await siteUser.PostAsync(T, new { type = "Rebox", siteId = other, partId = part, number = "MU-MS-WO2-" + tag })).IsSuccessStatusCode);

        // pickers and the site lists read the grants: all sites for the main site role, own site otherwise
        var g1 = (await mainUser.GetElAsync("/api/auth/session")).GetProperty("user").GetProperty("grants").EnumerateArray().ToList();
        Assert.All(g1, g => Assert.Equal(JsonValueKind.Null, g.GetProperty("siteId").ValueKind));
        var g2 = (await siteUser.GetElAsync("/api/auth/session")).GetProperty("user").GetProperty("grants").EnumerateArray().ToList();
        Assert.All(g2, g => Assert.Equal(plain, g.GetProperty("siteId").GetGuid()));

        // turned off, the role is back to its own site
        await admin.PutOkAsync($"/api/organization/sites/{main}/main", new { isMainSite = false });
        var (_, later) = await app.CreateUserAsync(Perms, main);
        Assert.NotEqual(HttpStatusCode.OK, (await later.GetAsync($"/api/sequfloh/parts/{part}")).StatusCode);
    }

    [Fact]
    public async Task A_department_role_at_the_main_site_stays_in_its_department()
    {
        var admin = await app.AdminAsync();
        var main = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        await admin.PutOkAsync($"/api/organization/sites/{main}/main", new { isMainSite = true });
        var dept = (await (await admin.PostOkAsync($"/api/organization/sites/{main}/departments", new { code = "D1", name = "Made-up dept" })).JsonAsync()).GetGuid();
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", Part("MU-MSD-" + Guid.NewGuid().ToString("N")[..8], other))).JsonAsync()).Id();
        var (_, u) = await app.CreateUserAsync(Perms, main, dept);
        Assert.NotEqual(HttpStatusCode.OK, (await u.GetAsync($"/api/sequfloh/parts/{part}")).StatusCode);
    }
}
