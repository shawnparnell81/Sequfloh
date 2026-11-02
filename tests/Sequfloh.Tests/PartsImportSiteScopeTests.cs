using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Importing parts needs both part permissions at the chosen site, as the administrator gave them. Made-up data only.</summary>
[Collection("api")]
public sealed class PartsImportSiteScopeTests(TestApp app)
{
    const string P = "/api/sequfloh/parts/import";

    async Task<object> ReadAllTabs(ApiClient c, Guid site)
    {
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await (await app.AdminAsync()).PostOkAsync("/api/sequfloh/reference/product-families", new { code = "MS_" + k, label = "Made-up Scope " + k, isActive = true, isDefault = false });
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Made-up Scope " + k);
        ws.Cell(1, 1).Value = "Part number"; ws.Cell(1, 2).Value = "Description";
        ws.Cell(2, 1).Value = "MU-SCOPE-" + Guid.NewGuid().ToString("N")[..6]; ws.Cell(2, 2).Value = "Made-up part";
        var ms = new MemoryStream(); wb.SaveAs(ms);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "made-up-scope.xlsx");
        var r = await c.Http.PostAsync($"{P}/read?allTabs=true", form);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        var t = JsonDocument.Parse(body).RootElement;
        return new { fileName = "made-up-scope.xlsx", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site,
            updateExisting = false, keepRepeats = false, tabs = t.GetProperty("tabs"), skippedTabs = t.GetProperty("skippedTabs") };
    }

    [Fact]
    public async Task A_role_for_one_site_previews_there_and_gets_a_clear_refusal_at_another_site()
    {
        var a = await app.CreateSiteAsync();
        var bCode = "SB" + Guid.NewGuid().ToString("N")[..6];
        var b = await app.CreateSiteAsync(bCode);
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], a);

        await eng.PostOkAsync($"{P}/preview", await ReadAllTabs(eng, a));

        var refused = await eng.Http.PostAsJsonAsync($"{P}/preview", await ReadAllTabs(eng, b));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var msg = await refused.Content.ReadAsStringAsync();
        Assert.Contains(bCode, msg);
        Assert.Contains(Permissions.SequflohEditParts, msg);
        Assert.Contains(Permissions.MasterDataEdit, msg);
    }

    [Fact]
    public async Task The_refusal_names_only_the_permission_missing_at_that_site()
    {
        var a = await app.CreateSiteAsync();
        var bCode = "SB" + Guid.NewGuid().ToString("N")[..6];
        var b = await app.CreateSiteAsync(bCode);
        // job tracking part editing for all sites, but "Add and edit parts" only for site A
        var (userId, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataView]);
        var admin = await app.AdminAsync();
        var roleId = await (await admin.PostOkAsync("/api/admin/roles", new { name = "R" + Guid.NewGuid().ToString("N"), description = (string?)null, permissions = new[] { Permissions.MasterDataEdit } })).Content.ReadFromJsonAsync<Guid>();
        await admin.PostOkAsync($"/api/admin/users/{userId}/roles", new { roleId, siteId = a, departmentId = (Guid?)null });

        await eng.PostOkAsync($"{P}/preview", await ReadAllTabs(eng, a));
        var refused = await eng.Http.PostAsJsonAsync($"{P}/preview", await ReadAllTabs(eng, b));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var msg = await refused.Content.ReadAsStringAsync();
        Assert.Contains(Permissions.MasterDataEdit, msg);
        Assert.DoesNotContain(Permissions.SequflohEditParts, msg);
    }
}
