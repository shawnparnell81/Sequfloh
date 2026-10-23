using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class DrawingImportTests(TestApp app)
{
    const string D = "/api/drawings";
    const string I = "/api/drawings/import";
    static readonly string[] Manager = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];
    static readonly string[] Viewer = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings];

    static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {text}\n%%EOF\n");

    async Task<string> PartAsync(Guid site, string? sensen = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = new Part { PartNumber = "IMP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Test pump", SiteId = site, Status = MasterStatus.Active, DrawingNumber = sensen };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return p.PartNumber;
    }

    /// <summary>Uploads one file the way the browser does on local storage: start, send the bytes, finish.</summary>
    static async Task<JsonElement> ImportFileAsync(ApiClient c, Guid folder, string name, byte[] bytes, IEnumerable<string> matchNames, string revision = "A")
    {
        var s = await (await c.PostOkAsync($"{I}/start", new { folderId = folder, fileName = name, size = bytes.LongLength })).JsonAsync();
        Assert.False(s.GetProperty("direct").GetBoolean());
        var put = await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        return await (await c.PostOkAsync($"{I}/finish", new { token = s.Str("token"), revision, linkParts = true, matchNames })).JsonAsync();
    }

    [Fact]
    public async Task A_folder_of_drawings_is_imported_with_its_tree_linked_to_parts_and_a_second_run_skips_it()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var byNumber = await PartAsync(site);
        var sensen = "SN-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PartAsync(site, sensen);
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Import " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();
        var a = Pdf("one"); var b = Pdf("two-longer");
        var files = new[] { new { path = $"Pumps/{byNumber}/cover.pdf", size = a.LongLength }, new { path = $"Pumps/Other/{sensen}.pdf", size = b.LongLength } };
        string[] folders = ["Pumps", $"Pumps/{byNumber}", "Pumps/Other"];

        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = true, files, folders })).JsonAsync();
        Assert.All(plan.GetProperty("files").EnumerateArray(), f => Assert.False(f.GetProperty("exists").GetBoolean()));
        Assert.Equal([byNumber], plan.GetProperty("folders").EnumerateArray().Single(f => f.Str("path") == $"Pumps/{byNumber}").GetProperty("parts").EnumerateArray().Select(x => x.GetString()));
        Assert.Single(plan.GetProperty("files").EnumerateArray().Single(f => f.Str("path").EndsWith(sensen + ".pdf")).GetProperty("parts").EnumerateArray());

        var ids = (await (await c.PostOkAsync($"{I}/folders", new { folderId = root, linkParts = true, paths = new[] { $"Pumps/{byNumber}", "Pumps/Other" } })).JsonAsync())
            .EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetGuid());
        var r1 = await ImportFileAsync(c, ids[$"Pumps/{byNumber}"], "cover.pdf", a, ["Pumps", byNumber]);
        var r2 = await ImportFileAsync(c, ids["Pumps/Other"], sensen + ".pdf", b, ["Pumps", "Other"], "-");
        Assert.Equal(1, r1.Int("linked")); Assert.Equal(1, r2.Int("linked"));

        // the tree is there, the part folder is linked, each file is a drawing titled with its file name
        var pumps = (await c.GetElAsync($"{D}/folders?parentId={root}")).GetProperty("folders").EnumerateArray().Single();
        Assert.Equal("Pumps", pumps.Str("name"));
        var inner = (await c.GetElAsync($"{D}/folders?parentId={pumps.Id()}")).GetProperty("folders").EnumerateArray().ToList();
        Assert.Equal(byNumber, inner.Single(f => f.Str("name") == byNumber).GetProperty("part").Str("partNumber"));
        var drawing = await c.GetElAsync($"{D}/{r2.Id()}");
        Assert.Equal(sensen, drawing.Str("title")); Assert.Equal("-", drawing.Str("currentRevision"));
        var file = await c.GetAsync($"{D}/{r2.Id()}/file");
        Assert.Equal(b, await file.Content.ReadAsByteArrayAsync());
        Assert.Single((await c.GetJsonAsync<List<JsonElement>>($"{D}/for-part?partNumber={byNumber}")));

        // picking the same folder again: both files are already there; finishing a duplicate upload anyway is skipped
        plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = true, files, folders })).JsonAsync();
        Assert.All(plan.GetProperty("files").EnumerateArray(), f => Assert.True(f.GetProperty("exists").GetBoolean()));
        Assert.All(plan.GetProperty("folders").EnumerateArray(), f => Assert.True(f.GetProperty("exists").GetBoolean()));
        Assert.True((await ImportFileAsync(c, ids["Pumps/Other"], sensen + ".pdf", b, [])).GetProperty("skipped").GetBoolean());

        await c.PostOkAsync($"{I}/summary", new { folderId = root, imported = 2, folders = 3, bytes = a.Length + b.Length, skipped = 1, failed = 0, linked = 2, cancelled = false });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.AsNoTracking().Where(x => x.EntityId == root && x.Action == "Imported drawings").SingleAsync();
        Assert.Contains("2 drawings", audit.Description); Assert.Contains("1 already there", audit.Description);
    }

    [Fact]
    public async Task Imports_check_the_file_the_token_and_the_permission()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var (_, viewer) = await app.CreateUserAsync(Viewer, site);
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Checks", siteId = site })).JsonAsync()).Id();

        // a file that is not really a PDF is refused and not kept
        var fake = Encoding.ASCII.GetBytes("not a pdf at all");
        var s = await (await c.PostOkAsync($"{I}/start", new { folderId = root, fileName = "fake.pdf", size = fake.LongLength })).JsonAsync();
        await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(fake));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/finish", new { token = s.Str("token"), revision = "A", linkParts = false })).StatusCode);

        // a size that does not match what was uploaded is refused
        s = await (await c.PostOkAsync($"{I}/start", new { folderId = root, fileName = "short.pdf", size = 999L })).JsonAsync();
        await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(Pdf("short")));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/finish", new { token = s.Str("token"), revision = "A", linkParts = false })).StatusCode);

        // unknown types and changed tokens are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/start", new { folderId = root, fileName = "notes.txt", size = 10L })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/finish", new { token = s.Str("token") + "x", revision = "A", linkParts = false })).StatusCode);

        // viewing drawings is not enough to import
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{I}/plan", new { folderId = root, files = Array.Empty<object>(), linkParts = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{I}/start", new { folderId = root, fileName = "a.pdf", size = 10L })).StatusCode);
    }
}
