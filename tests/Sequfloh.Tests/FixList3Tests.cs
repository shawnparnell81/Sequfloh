using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Completed forms imported straight into a product family on Completed Validation/FAI, and archive and delete of them. Made-up data only.</summary>
[Collection("api")]
public sealed class FixList3Tests(TestApp app)
{
    static readonly string[] Importer = [Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles, Permissions.SequflohDeleteRecords];

    static async Task<HttpResponseMessage> ImportAsync(ApiClient c, Guid site, string name, Guid? family, bool completed = true)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,OK\n")), "file", name }, { new StringContent(site.ToString()), "siteId" },
            { new StringContent(completed ? "true" : "false"), "completedForm" } };
        if (family is { } f) form.Add(new StringContent(f.ToString()), "familyId");
        return await c.Http.PostAsync("/api/imported-files", form);
    }

    [Fact]
    public async Task A_file_imported_on_a_family_tab_is_a_completed_form_in_that_family_and_archive_and_delete_are_audited()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Importer, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "WD" + tag, label = "Widget " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();

        var up = await ImportAsync(c, site, $"Widget {tag} done.csv", fam);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var id = (await up.JsonAsync()).Id();
        var file = await c.GetElAsync($"/api/imported-files/{id}");
        Assert.False(file.GetProperty("isBlankForm").GetBoolean());
        Assert.True(file.GetProperty("isCompletedForm").GetBoolean());
        Assert.Equal(fam, file.GetProperty("familyId").GetGuid());
        var listed = (await c.GetElAsync($"/api/imported-files?kind=filled&family={fam}")).GetProperty("items").EnumerateArray().Select(x => x.Id()).ToList();
        Assert.Contains(id, listed);

        // unassigned import, then moved to the family; a family only goes with a completed form
        var un = (await (await ImportAsync(c, site, $"Widget {tag} loose.csv", null)).JsonAsync()).Id();
        Assert.Contains(un, (await c.GetElAsync("/api/imported-files?kind=filled&family=none&pageSize=500")).GetProperty("items").EnumerateArray().Select(x => x.Id()));
        Assert.True((await c.PutAsync($"/api/imported-files/{un}/family", new { familyId = fam })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(c, site, "x.csv", fam, completed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(c, site, "y.csv", Guid.NewGuid())).StatusCode);

        Assert.True((await c.PostAsync($"/api/imported-files/{id}/archive", new { })).IsSuccessStatusCode);
        Assert.DoesNotContain(id, (await c.GetElAsync($"/api/imported-files?kind=filled&family={fam}")).GetProperty("items").EnumerateArray().Select(x => x.Id()));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/imported-files/{un}")).StatusCode);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == id || a.EntityId == un).ToListAsync();
        Assert.Contains(audit, a => a.EntityId == id && a.Description.Contains($"into Widget {tag}") && a.UserName.Length > 0);
        Assert.Contains(audit, a => a.EntityId == id && a.Action == "Archived");
        Assert.Contains(audit, a => a.EntityId == un && a.Description.Contains("Deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_pdf_imported_on_a_family_tab_is_stored_as_uploaded_and_opens_inline()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Importer, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "PD" + tag, label = "Pdf fam " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", $"Final FAI {tag}.pdf" }, { new StringContent(site.ToString()), "siteId" },
            { new StringContent("true"), "completedForm" }, { new StringContent(fam.ToString()), "familyId" } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var id = (await up.JsonAsync()).Id();
        var file = await c.GetElAsync($"/api/imported-files/{id}");
        Assert.Equal("pdf", file.GetProperty("kind").GetString());
        Assert.Equal($"Final FAI {tag}", file.GetProperty("title").GetString());
        Assert.Contains(id, (await c.GetElAsync($"/api/imported-files?kind=filled&family={fam}")).GetProperty("items").EnumerateArray().Select(x => x.Id()));
        var view = await c.Http.GetAsync($"/api/imported-files/{id}/download?view=true");
        Assert.Equal("application/pdf", view.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("inline", view.Content.Headers.ContentDisposition?.ToString() ?? view.Headers.GetValues("Content-Disposition").First());
        Assert.Equal(bytes, await view.Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes, await (await c.Http.GetAsync($"/api/imported-files/{id}/download?original=true")).Content.ReadAsByteArrayAsync());
        // a PDF only goes in as a completed form, and must really be a PDF
        var plain = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "loose.pdf" }, { new StringContent(site.ToString()), "siteId" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Http.PostAsync("/api/imported-files", plain)).StatusCode);
        var fake = new MultipartFormDataContent { { new ByteArrayContent(Encoding.ASCII.GetBytes("not a pdf")), "file", "fake.pdf" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "completedForm" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.Http.PostAsync("/api/imported-files", fake)).StatusCode);
        Assert.True((await c.PostAsync($"/api/imported-files/{id}/archive", new { })).IsSuccessStatusCode);
    }
}
