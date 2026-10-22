using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Editable work instruction blocks and reusable templates.</summary>
[Collection("api")]
public sealed class SequflohBlockTests(TestApp app)
{
    static readonly string[] AuthorPerms = [Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction];

    static object Save(JsonElement wi, Guid site, object[] blocks) => new
    {
        title = wi.GetProperty("instruction").Str("title"), siteId = site, departmentId = (Guid?)null, ownerId = (Guid?)null, partId = (Guid?)null,
        content = new { }, steps = Array.Empty<object>(), characteristics = Array.Empty<object>(), partsRows = Array.Empty<object>(), blocks
    };

    static readonly object[] Layout =
    [
        new { kind = "text", title = "Scope", body = "Seal replacement on the inlet valve." },
        new { kind = "caution", level = "warning", body = "Isolate and drain the line before opening it." },
        new { kind = "step", operation = "10", title = "Remove the cover", body = "Loosen the four bolts in a cross pattern.", tools = "13 mm socket" },
        new { kind = "image", caption = "Cover bolt pattern", alt = "Four bolts numbered 1 to 4" },
        new { kind = "table", title = "Torque", columns = new[] { "Bolt", "Torque (N m)" }, rows = new[] { new[] { "1-4", "25" } } },
        new { kind = "step", operation = "20", title = "Fit the new seal" },
        new { kind = "check", label = "Leak test passed at 6 bar", check = "check", signer = "Inspector", required = true },
    ];

    [Fact]
    public async Task Authors_arrange_blocks_and_reuse_them_as_templates()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync(AuthorPerms, site);
        var (_, other) = await app.CreateUserAsync(AuthorPerms, site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var (_, admin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings], site);

        var id = (await (await author.PostOkAsync("/api/sequfloh/instructions", new { type = "RepairWork", title = "Valve seal repair", siteId = site })).JsonAsync()).Id();
        var url = $"/api/sequfloh/instructions/{id}";
        var wi = await author.GetElAsync(url);
        Assert.Equal(0, wi.GetProperty("instruction").GetProperty("blocks").GetArrayLength());

        // invalid blocks are refused: unknown kind, a step without an operation number, a repeated operation number
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(url, Save(wi, site, [new { kind = "video", body = "x" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(url, Save(wi, site, [new { kind = "step", title = "No number" }]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(url, Save(wi, site, [new { kind = "step", operation = "10", title = "A" }, new { kind = "step", operation = "10", title = "B" }]))).StatusCode);

        // save, then reorder by sending the blocks in a new order and remove one
        Assert.True((await author.PutAsync(url, Save(wi, site, Layout))).IsSuccessStatusCode);
        wi = await author.GetElAsync(url);
        var blocks = wi.GetProperty("instruction").GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(["text", "caution", "step", "image", "table", "step", "check"], blocks.Select(b => b.Str("kind")));
        Assert.Equal("warning", blocks[1].Str("level"));
        var reordered = new object[] { blocks[2], blocks[0], blocks[1], blocks[4], blocks[5], blocks[6] }.Select(b => (object)JsonSerializer.Deserialize<Dictionary<string, object>>(((JsonElement)b).GetRawText())!).ToArray();
        Assert.True((await author.PutAsync(url, Save(wi, site, reordered))).IsSuccessStatusCode);
        wi = await author.GetElAsync(url);
        var after = wi.GetProperty("instruction").GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(6, after.Count);
        Assert.Equal(blocks[2].Str("id"), after[0].Str("id"));   // ids survive reordering
        var print = await author.GetAsync($"{url}/print");
        Assert.Equal(HttpStatusCode.OK, print.StatusCode);
        Assert.Equal("application/pdf", print.Content.Headers.ContentType?.MediaType);

        // save as a reusable template; a viewer cannot
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{url}/save-as-template", new { name = "Valve seal layout" })).StatusCode);
        var tpl = await (await author.PostOkAsync($"{url}/save-as-template", new { name = "Valve seal layout", description = "Cover, seal, leak test" })).JsonAsync();
        var tplId = tpl.Id();
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsync($"{url}/save-as-template", new { name = "Valve seal layout" })).StatusCode);
        var list = await viewer.GetElAsync("/api/sequfloh/instruction-templates?type=RepairWork");
        Assert.Contains(list.EnumerateArray(), t => t.Str("name") == "Valve seal layout");

        // a new instruction starts from the template with fresh block ids
        var id2 = (await (await other.PostOkAsync("/api/sequfloh/instructions", new { type = "RepairWork", title = "Second valve", siteId = site, templateId = tplId })).JsonAsync()).Id();
        var wi2 = await other.GetElAsync($"/api/sequfloh/instructions/{id2}");
        var b2 = wi2.GetProperty("instruction").GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal(6, b2.Count);
        Assert.NotEqual(after[0].Str("id"), b2[0].Str("id"));
        Assert.Equal("Valve seal layout", wi2.GetProperty("instruction").Str("template"));
        // the template is for repair work only
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Wrong type", siteId = site, templateId = tplId })).StatusCode);

        // appending the same template again would repeat operation numbers; replace starts over
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync($"/api/sequfloh/instructions/{id2}/apply-template", new { templateId = tplId, replace = false })).StatusCode); // appending repeats operation numbers
        var replaced = await (await other.PostOkAsync($"/api/sequfloh/instructions/{id2}/apply-template", new { templateId = tplId, replace = true })).JsonAsync();
        Assert.Equal(6, replaced.GetProperty("blocks").GetArrayLength());

        // only the owner or a settings manager changes or retires a template
        var t = await author.GetElAsync($"/api/sequfloh/instruction-templates/{tplId}");
        var change = new { name = "Valve seal layout", description = "Updated", type = "RepairWork", blocks = Layout };
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsync($"/api/sequfloh/instruction-templates/{tplId}", change)).StatusCode);
        Assert.True((await author.PutAsync($"/api/sequfloh/instruction-templates/{tplId}", change)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/sequfloh/instruction-templates/{tplId}/retire", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/sequfloh/instruction-templates/{tplId}/retire", new { })).StatusCode);
        Assert.DoesNotContain((await viewer.GetElAsync("/api/sequfloh/instruction-templates")).EnumerateArray(), x => x.Str("name") == "Valve seal layout");
        _ = t;

        // audit entries read as plain sentences
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == tplId).OrderBy(a => a.Id).Select(a => a.Description).ToListAsync();
        Assert.Contains(audit, d => d.Contains("work instruction template Valve seal layout"));
    
    }

    [Fact]
    public async Task Image_blocks_show_only_pictures_attached_to_the_instruction()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync(AuthorPerms, site);
        var a = (await (await author.PostOkAsync("/api/sequfloh/instructions", new { type = "Fai", title = "Bracket FAI", siteId = site })).JsonAsync()).Id();
        var b = (await (await author.PostOkAsync("/api/sequfloh/instructions", new { type = "Fai", title = "Other FAI", siteId = site })).JsonAsync()).Id();
        var up = await author.UploadAsync("SequflohWorkInstruction", a, "datum-scheme.pdf", "%PDF datum scheme");
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var file = (await author.GetElAsync($"/api/records/SequflohWorkInstruction/{a}/files")).EnumerateArray().Single().Id();

        var wiA = await author.GetElAsync($"/api/sequfloh/instructions/{a}");
        Assert.True((await author.PutAsync($"/api/sequfloh/instructions/{a}", Save(wiA, site, [new { kind = "image", caption = "Datum scheme", attachmentId = file }]))).IsSuccessStatusCode);
        var wiB = await author.GetElAsync($"/api/sequfloh/instructions/{b}");
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync($"/api/sequfloh/instructions/{b}", Save(wiB, site, [new { kind = "image", caption = "Borrowed", attachmentId = file }]))).StatusCode);

        // a template keeps the caption but not the picture
        var tpl = await (await author.PostOkAsync($"/api/sequfloh/instructions/{a}/save-as-template", new { name = $"FAI datum layout {a:N}" })).JsonAsync();
        var block = (await author.GetElAsync($"/api/sequfloh/instruction-templates/{tpl.Id()}")).GetProperty("blocks")[0];
        Assert.Equal("Datum scheme", block.Str("caption"));
        Assert.False(block.TryGetProperty("attachmentId", out _));
    }
}
