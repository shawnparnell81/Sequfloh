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

    [Fact]
    public async Task A_folder_imported_at_the_top_level_makes_its_tree_there_links_parts_and_resumes()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var pn = await PartAsync(site);
        var a = Pdf("top-one"); var b = Pdf("top-two-longer");
        var files = new[] { new { path = $"Line 1/{pn}/cover.pdf", size = a.LongLength }, new { path = "Line 1/Fixtures/plate.pdf", size = b.LongLength } };
        string[] folders = ["Line 1", $"Line 1/{pn}", "Line 1/Fixtures"];

        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = (Guid?)null, linkParts = true, files, folders })).JsonAsync();
        Assert.All(plan.GetProperty("folders").EnumerateArray(), f => Assert.False(f.GetProperty("exists").GetBoolean()));
        var ids = (await (await c.PostOkAsync($"{I}/folders", new { folderId = (Guid?)null, linkParts = true, paths = new[] { $"Line 1/{pn}", "Line 1/Fixtures" } })).JsonAsync())
            .EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetGuid());
        Assert.Equal(1, (await ImportFileAsync(c, ids[$"Line 1/{pn}"], "cover.pdf", a, ["Line 1", pn])).Int("linked"));
        await ImportFileAsync(c, ids["Line 1/Fixtures"], "plate.pdf", b, ["Line 1", "Fixtures"]);

        // "Line 1" is a top-level folder of the site, its subfolders sit under it, the part folder is linked
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.DrawingFolders.AsNoTracking().SingleAsync(f => f.SiteId == site && f.Name == "Line 1");
            Assert.Null(line.ParentId);
            Assert.Equal(2, await db.DrawingFolders.CountAsync(f => f.ParentId == line.Id));
        }
        Assert.Single(await c.GetJsonAsync<List<JsonElement>>($"{D}/for-part?partNumber={pn}"));

        // choosing the same folder again finds the folders and files already there, without making a second tree
        plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = (Guid?)null, linkParts = true, files, folders })).JsonAsync();
        Assert.All(plan.GetProperty("files").EnumerateArray(), f => Assert.True(f.GetProperty("exists").GetBoolean()));
        Assert.All(plan.GetProperty("folders").EnumerateArray(), f => Assert.True(f.GetProperty("exists").GetBoolean()));
        var again = (await (await c.PostOkAsync($"{I}/folders", new { folderId = (Guid?)null, linkParts = true, paths = new[] { "Line 1/Fixtures" } })).JsonAsync()).GetProperty("Line 1/Fixtures").GetGuid();
        Assert.Equal(ids["Line 1/Fixtures"], again);

        await c.PostOkAsync($"{I}/summary", new { folderId = (Guid?)null, imported = 2, folders = 3, bytes = a.Length + b.Length, skipped = 0, failed = 0, linked = 1, cancelled = false });
    }

    async Task<List<Guid>> FuelPumpsAsync(Guid site, string jh, int count)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = new List<Guid>();
        for (var n = 0; n < count; n++)
        {
            var p = new Part { PartNumber = "FPX-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Fuel Pump", ProductFamily = "Fuel Pump", SiteId = site, Status = MasterStatus.Active,
                Attributes = JsonSerializer.Serialize(new Dictionary<string, string> { ["napaPn"] = "NP" + n, ["jhPn"] = jh }) };
            db.Parts.Add(p);
            ids.Add(p.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    async Task<HashSet<Guid>> LinksAsync(Guid drawing)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.DrawingPartLinks.Where(l => l.DrawingId == drawing).Select(l => l.PartId).ToListAsync()).ToHashSet();
    }

    [Fact]
    public async Task A_drawing_named_with_a_JH_PN_links_to_every_fuel_pump_with_that_JH_PN()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var pumps = await FuelPumpsAsync(site, $"JH-{tag}", 7);
        var other = await FuelPumpsAsync(site, $"JH-{tag}9", 1);
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Pumps " + tag, siteId = site })).JsonAsync()).Id();
        var bytes = Pdf("jh");
        // the file is named with a space instead of the dash and in lower case: still the same drawing number
        var name = $"jh {tag.ToLowerInvariant()}.pdf";

        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = true, files = new[] { new { path = name, size = bytes.LongLength } }, folders = Array.Empty<string>() })).JsonAsync();
        Assert.Equal(7, plan.GetProperty("files")[0].GetProperty("parts").GetArrayLength());

        var r = await ImportFileAsync(c, root, name, bytes, []);
        Assert.Equal(7, r.Int("linked"));
        var links = await LinksAsync(r.Id());
        Assert.True(links.SetEquals(pumps));
        Assert.DoesNotContain(other[0], links);
    }

    [Fact]
    public async Task Relink_adds_missing_links_once_and_keeps_links_made_by_hand()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var (_, viewer) = await app.CreateUserAsync(Viewer, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Relink " + tag, siteId = site })).JsonAsync()).Id();
        // the drawing comes in before its parts exist
        var d = (await ImportFileAsync(c, root, $"JH{tag}.pdf", Pdf("relink"), [])).Id();
        Assert.Empty(await LinksAsync(d));
        var manual = await FuelPumpsAsync(site, "JH-UNRELATED-" + tag, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync($"{D}/{d}/parts", new { ids = manual })).StatusCode);

        // parts added straight to the database (not through the part screens), so only a relink links them
        var pumps = await FuelPumpsAsync(site, $"JH_{tag}", 3);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{D}/relink", new { })).StatusCode);
        var first = await (await c.PostOkAsync($"{D}/relink", new { })).JsonAsync();
        Assert.True(first.Int("checked") >= 1);
        Assert.Equal(3, first.Int("added"));
        Assert.True((await LinksAsync(d)).SetEquals(pumps.Concat(manual)));

        var second = await (await c.PostOkAsync($"{D}/relink", new { })).JsonAsync();
        Assert.Equal(0, second.Int("added"));
        Assert.Equal(4, (await LinksAsync(d)).Count);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == d && a.Action == "Linked drawing to parts").ToListAsync();
        var entry = Assert.Single(audit);
        Assert.Contains("3 more parts", entry.Description);
        Assert.NotNull(entry.UserName);
    }

    [Theory]
    [InlineData("/")] [InlineData("-")] [InlineData(" ")] [InlineData("N/A")] [InlineData("n/a.pdf")] [InlineData("--")]
    public void Placeholders_are_never_a_number_to_match(string value) => Assert.Equal("", Sequfloh.Drawings.DrawingPartMatcher.Normalize(value));

    [Fact]
    public async Task A_slash_placeholder_never_links_parts_or_drawings_but_the_JH_PN_does()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var jh = $"JH-390104Q{tag}";
        Guid a, b;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Part Pump(string or, string jhPn) => new() { PartNumber = or, Name = "Fuel Pump", ProductFamily = "Fuel Pump", SiteId = site, Status = MasterStatus.Active,
                Attributes = JsonSerializer.Serialize(new Dictionary<string, string> { ["napaPn"] = "/", ["dmaPn"] = "P50029", ["cross"] = "P72128", ["jhPn"] = jhPn, ["boxSize"] = "180*120*120" }) };
            var pa = Pump($"E16070{tag}", jh); var pb = Pump($"E16071{tag}", "/");
            db.Parts.AddRange(pa, pb);
            await db.SaveChangesAsync();
            a = pa.Id; b = pb.Id;
        }
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Slash " + tag, siteId = site })).JsonAsync()).Id();
        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = true, files = new[] { new { path = "N-A.pdf", size = 10L }, new { path = "-.pdf", size = 10L } }, folders = new[] { "-" } })).JsonAsync();
        Assert.All(plan.GetProperty("files").EnumerateArray(), f => Assert.Equal(0, f.GetProperty("parts").GetArrayLength()));
        Assert.All(plan.GetProperty("folders").EnumerateArray(), f => Assert.Equal(0, f.GetProperty("parts").GetArrayLength()));

        var na = (await ImportFileAsync(c, root, "N-A.pdf", Pdf("na"), ["/", "-"])).Id();
        var good = await ImportFileAsync(c, root, jh + ".pdf", Pdf("jh390104q"), []);
        Assert.Equal(1, good.Int("linked"));
        Assert.Equal([a], await LinksAsync(good.Id()));
        await c.PostOkAsync($"{D}/relink", new { });
        Assert.Empty(await LinksAsync(na));
        Assert.DoesNotContain(b, await LinksAsync(good.Id()));
    }
}
