using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Engineering, Development line workspace: line procedures and Development work orders. Made-up data only.</summary>
[Collection("api")]
public sealed class DevelopmentWorkspaceTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Engineer = [Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction, Permissions.SequflohCreateProductionTask, Permissions.SequflohReviseWorkInstruction];

    static async Task<HttpResponseMessage> Import(ApiClient c, Guid site, string name, string? line)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 made-up"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", name);
        form.Add(new StringContent("Procedure"), "type");
        form.Add(new StringContent(site.ToString()), "siteId");
        if (line is not null) form.Add(new StringContent(line), "developmentLine");
        return await c.Http.PostAsync("/api/sequfloh/instructions/import", form);
    }

    [Fact]
    public async Task Development_work_orders_number_as_WO_DEV_family_year_and_count()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"D{tag}", label = $"Dev family {tag}", description = (string?)null })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = $"c{tag}" })).IsSuccessStatusCode);
        var (_, c) = await app.CreateUserAsync(Engineer, site);
        var yy = (DateTime.Now.Year % 100).ToString("00");
        Assert.Equal($"WO-DEV-C{tag}{yy}-001", (await c.GetElAsync($"{T}/next-number?type=Development&familyId={fam}&siteId={site}")).GetProperty("number").GetString());
        async Task<System.Text.Json.JsonElement> Create(object body) { var id = (await (await c.PostOkAsync(T, body)).JsonAsync()).Id(); return (await c.GetElAsync($"{T}/{id}")).GetProperty("record"); }
        var r1 = await Create(new { type = "Development", siteId = site, familyId = fam, developmentLine = "csa", quantity = 2, number = (string?)null });
        Assert.Equal($"WO-DEV-C{tag}{yy}-001", r1.GetProperty("number").GetString());
        Assert.Equal("Development", r1.GetProperty("type").GetString());
        Assert.Equal("csa", r1.GetProperty("developmentLine").GetString());
        Assert.Equal($"WO-DEV-C{tag}{yy}-002", (await Create(new { type = "Development", siteId = site, familyId = fam, developmentLine = "gdi", number = (string?)null })).GetProperty("number").GetString());
        // no family code (for example Legacy without a family): the number is typed, as for other types
        Assert.Equal(JsonValueKindNull, (await c.GetElAsync($"{T}/next-number?type=Development&siteId={site}")).GetProperty("number").ValueKind);
        Assert.Equal($"LEG-{tag}", (await Create(new { type = "Development", siteId = site, developmentLine = "legacy", number = $"LEG-{tag}" })).GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(T, new { type = "Development", siteId = site, developmentLine = "nope", number = $"X-{tag}" })).StatusCode);
        // the code comes from Tracking settings, Work order number code: Development
        var (_, settingsAdmin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var tracking = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>((await settingsAdmin.GetElAsync("/api/sequfloh/settings")).GetProperty("tracking").GetRawText())!;
        Assert.Equal("DEV", tracking["developmentNumberCode"]?.ToString());
        try
        {
            tracking["developmentNumberCode"] = "eng";
            Assert.Equal(HttpStatusCode.NoContent, (await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 })).StatusCode);
            Assert.Equal($"WO-ENG-C{tag}{yy}-001", (await Create(new { type = "Development", siteId = site, familyId = fam, developmentLine = "csa", number = (string?)null })).GetProperty("number").GetString());
        }
        finally
        {
            tracking["developmentNumberCode"] = "DEV";
            await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 });
        }
        Assert.Equal($"WO-DEV-C{tag}{yy}-003", (await c.GetElAsync($"{T}/next-number?type=Development&familyId={fam}&siteId={site}")).GetProperty("number").GetString());
        // listed in the Development folder and the type filter
        var list = await c.GetElAsync($"{T}?folder=development&pageSize=200");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("number").GetString() == $"WO-DEV-C{tag}{yy}-001");
    }
    static readonly System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task A_procedure_imported_in_a_line_shows_only_there_and_attaches_to_a_Development_work_order_of_that_line()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Engineer, site);
        var title = "Dev proc " + Guid.NewGuid().ToString("N")[..6];
        var r = await Import(c, site, title + ".pdf", "csa");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var wid = (await r.JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(c, site, "x.pdf", "nope")).StatusCode);

        var inLine = await c.GetElAsync($"/api/sequfloh/instructions?folder=all&developmentLine=csa&pageSize=200");
        Assert.Contains(inLine.GetProperty("items").EnumerateArray(), x => x.Id() == wid);
        var other = await c.GetElAsync($"/api/sequfloh/instructions?folder=all&developmentLine=gdi&pageSize=200");
        Assert.DoesNotContain(other.GetProperty("items").EnumerateArray(), x => x.Id() == wid);
        foreach (var folder in new[] { "all", "procedure", "drafts" })
            Assert.DoesNotContain((await c.GetElAsync($"/api/sequfloh/instructions?folder={folder}&pageSize=200")).GetProperty("items").EnumerateArray(), x => x.Id() == wid);

        Guid general;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEntries.AsNoTracking().AnyAsync(a => a.EntityId == wid && a.Description.Contains("into CSA Development")));
            var wi = await db.SequflohWorkInstructions.SingleAsync(x => x.Id == wid);
            wi.Status = WorkInstructionStatus.Released;
            var g = new SequflohWorkInstruction { Type = WorkInstructionType.Procedure, Title = "General " + title, Number = "GEN-" + title, Status = WorkInstructionStatus.Released, SiteId = site };
            g.FamilyId = g.Id; general = g.Id;
            db.SequflohWorkInstructions.Add(g);
            await db.SaveChangesAsync();
        }

        Guid[] Ids(System.Text.Json.JsonElement e) => e.EnumerateArray().Select(x => x.Id()).ToArray();
        var offered = Ids(await c.GetElAsync($"{T}/instructions?type=Development&line=csa"));
        Assert.Contains(wid, offered); Assert.Contains(general, offered);
        Assert.DoesNotContain(wid, Ids(await c.GetElAsync($"{T}/instructions?type=Development&line=gdi")));
        Assert.DoesNotContain(wid, Ids(await c.GetElAsync($"{T}/instructions?type=Inspection")));

        var ok = await c.PostAsync(T, new { type = "Development", siteId = site, developmentLine = "csa", workInstructionId = wid, number = "DV-" + title });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(T, new { type = "Development", siteId = site, developmentLine = "gdi", workInstructionId = wid, number = "DG-" + title })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", workInstructionId = wid, number = "IN-" + title })).StatusCode);
        Assert.True((await c.PostAsync(T, new { type = "Development", siteId = site, developmentLine = "gdi", workInstructionId = general, number = "DGG-" + title })).IsSuccessStatusCode);

        // a new revision keeps the line
        var rev = await c.PostAsync($"/api/sequfloh/instructions/{wid}/revise", new { reason = "Made-up change" });
        Assert.True(rev.IsSuccessStatusCode, await rev.Content.ReadAsStringAsync());
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.All(await db.SequflohWorkInstructions.AsNoTracking().Where(x => x.FamilyId == wid).ToListAsync(), x => Assert.Equal("csa", x.DevelopmentLine));
        }
    }
}
