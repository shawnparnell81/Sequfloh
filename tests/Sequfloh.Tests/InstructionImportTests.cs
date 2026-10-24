using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Import file / Import folder on the work instruction lists: one draft work instruction per file, with the file attached. Synthetic data only.</summary>
[Collection("api")]
public sealed class InstructionImportTests(TestApp app)
{
    static readonly string[] Author = [Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction];

    static async Task<HttpResponseMessage> Import(ApiClient c, Guid site, string name, string body, string type = "RepairWork")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent(type), "type");
        form.Add(new StringContent(site.ToString()), "siteId");
        return await c.Http.PostAsync("/api/sequfloh/instructions/import", form);
    }

    [Fact]
    public async Task Each_file_becomes_a_draft_work_instruction_with_the_file_attached_and_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Author, site);
        var r = await Import(c, site, "Example pump rebuild.txt", "Made-up steps");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var id = (await r.JsonAsync()).Id();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = await db.SequflohWorkInstructions.AsNoTracking().SingleAsync(w => w.Id == id);
        Assert.Equal("Example pump rebuild", wi.Title);
        var att = await db.Attachments.AsNoTracking().SingleAsync(a => a.OwnerType == "SequflohWorkInstruction" && a.OwnerId == id);
        Assert.Equal("Example pump rebuild.txt", att.FileName);
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.EntityId == att.Id));
        Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.EntityId == id));
        // nothing on work orders
        Assert.False(await db.Attachments.AsNoTracking().AnyAsync(a => a.OwnerId == id && a.OwnerType != "SequflohWorkInstruction"));
    }

    [Fact]
    public async Task A_file_type_that_cannot_be_attached_is_refused_and_creates_nothing()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Author, site);
        var title = "Example script " + Guid.NewGuid().ToString("N")[..6];
        var r = await Import(c, site, title + ".exe", "MZ");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SequflohWorkInstructions.AsNoTracking().AnyAsync(w => w.Title == title));
    }

    [Fact]
    public async Task Import_needs_the_create_work_instruction_permission()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var r = await Import(c, site, "Example.txt", "x");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
