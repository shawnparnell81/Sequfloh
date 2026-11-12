using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A part folder holding the part's drawing plus old and reference copies: every file is imported, only the drawing named for the folder is linked.</summary>
[Collection("api")]
public sealed class DrawingPrimaryTests(TestApp app)
{
    const string D = "/api/drawings";
    const string I = "/api/drawings/import";
    static readonly string[] Manager = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];
    static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {text}\n%%EOF\n");

    async Task PartsAsync(Guid site, params string[] numbers)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var n in numbers) db.Parts.Add(new Part { PartNumber = n, Name = "Strut " + n, SiteId = site, Status = MasterStatus.Active });
        await db.SaveChangesAsync();
    }

    static async Task<JsonElement> ImportAsync(ApiClient c, Guid folder, string name, byte[] bytes, string[] matchNames, bool? primary)
    {
        var s = await (await c.PostOkAsync($"{I}/start", new { folderId = folder, fileName = name, size = bytes.LongLength })).JsonAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(bytes))).StatusCode);
        return await (await c.PostOkAsync($"{I}/finish", new { token = s.Str("token"), revision = "A", linkParts = true, matchNames, primary })).JsonAsync();
    }

    async Task<int> DrawingsInAsync(Guid folder)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Drawings.CountAsync(d => d.FolderId == folder && !d.IsArchived);
    }

    async Task<List<string>> ForPartAsync(ApiClient c, string pn) =>
        (await c.GetJsonAsync<List<JsonElement>>($"{D}/for-part?partNumber={pn}")).Select(x => x.Str("fileName")).OrderBy(x => x).ToList();

    [Fact]
    public async Task Every_file_in_a_part_folder_is_imported_and_only_the_one_named_for_the_folder_is_linked()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string pn = $"P{tag}-0017", other = $"P{tag}-0571", spring = $"S{tag}40740";
        await PartsAsync(site, pn, other, spring);
        var family = $"CSA {tag}";
        var ids = (await (await c.PostOkAsync($"{I}/folders", new { folderId = (Guid?)null, linkParts = true, paths = new[] { $"{family}/{pn}" } })).JsonAsync())
            .EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetGuid());
        var folder = ids[$"{family}/{pn}"];

        // the same three files the drive holds for 9212-0017; only the one named for the folder is the primary drawing
        var r1 = await ImportAsync(c, folder, $"{spring}.pdf", Pdf("spring"), [family, pn], false);
        var r2 = await ImportAsync(c, folder, $"{other}.pdf", Pdf("other-part"), [family, pn], false);
        var r3 = await ImportAsync(c, folder, $"{pn}.pdf", Pdf("the-part"), [family, pn], true);
        Assert.Equal(3, await DrawingsInAsync(folder));
        Assert.Equal(0, r1.Int("linked")); Assert.Equal(0, r2.Int("linked")); Assert.Equal(1, r3.Int("linked"));
        Assert.Equal([$"{pn}.pdf"], await ForPartAsync(c, pn));
        Assert.Empty(await ForPartAsync(c, spring));

        // Relink keeps the reference copies unlinked
        await c.PostOkAsync($"{D}/relink", new { });
        Assert.Equal([$"{pn}.pdf"], await ForPartAsync(c, pn));
        Assert.Empty(await ForPartAsync(c, other));
    }

    [Fact]
    public async Task The_primary_link_check_lists_then_fixes_links_made_by_the_earlier_import_without_touching_files()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string pn = $"Q{tag}-0001", spring = $"R{tag}60123", springs = $"CS{tag}";
        await PartsAsync(site, pn, spring, springs);
        var family = $"CSA {tag}";
        var ids = (await (await c.PostOkAsync($"{I}/folders", new { folderId = (Guid?)null, linkParts = true, paths = new[] { $"{family}/{pn}", $"{family}/{springs}" } })).JsonAsync())
            .EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetGuid());

        // the way the import worked before: no primary flag, so every file in the part folder was linked to the part
        await ImportAsync(c, ids[$"{family}/{pn}"], $"{pn} OLD.pdf", Pdf("old"), [family, pn], null);
        await ImportAsync(c, ids[$"{family}/{pn}"], $"{spring} Coil spring.pdf", Pdf("coil"), [family, pn], null);
        await ImportAsync(c, ids[$"{family}/{pn}"], $"{pn}.pdf", Pdf("current"), [family, pn], null);
        // two files, neither named for the folder: left for a person to choose
        await ImportAsync(c, ids[$"{family}/{springs}"], $"{springs}-DRIVR.pdf", Pdf("driver"), [family, springs], null);
        await ImportAsync(c, ids[$"{family}/{springs}"], $"{springs}-PASS.pdf", Pdf("passenger-side"), [family, springs], null);
        Assert.Equal(3, (await ForPartAsync(c, pn)).Count);

        var dry = await (await c.PostOkAsync($"{I}/primary-links", new { apply = false })).JsonAsync();
        Assert.False(dry.GetProperty("applied").GetBoolean());
        var mine = dry.GetProperty("unlinkedDrawings").EnumerateArray().Where(x => x.Str("folder").StartsWith(family)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains(dry.GetProperty("needsChoice").EnumerateArray(), x => x.Str("folder") == $"{family}/{springs}");
        Assert.Equal(3, (await ForPartAsync(c, pn)).Count);

        var applied = await (await c.PostOkAsync($"{I}/primary-links", new { apply = true })).JsonAsync();
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.Equal([$"{pn}.pdf"], await ForPartAsync(c, pn));
        Assert.Equal(3, await DrawingsInAsync(ids[$"{family}/{pn}"]));
        // the folder with no file named for it keeps both drawings on the part
        Assert.Equal(2, (await ForPartAsync(c, springs)).Count);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var titles = await db.Drawings.AsNoTracking().Where(d => d.FolderId == ids[$"{family}/{pn}"]).Select(d => d.Title + "|" + d.CurrentRevision).OrderBy(x => x).ToListAsync();
        Assert.Equal([$"{pn} OLD|A", $"{pn}|A", $"{spring} Coil spring|A"], titles.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "Unlinked drawing from parts" && a.Description.Contains(pn + " OLD")));

        // running it again changes nothing
        var again = await (await c.PostOkAsync($"{I}/primary-links", new { apply = true })).JsonAsync();
        Assert.DoesNotContain(again.GetProperty("unlinkedDrawings").EnumerateArray(), x => x.Str("folder").StartsWith(family));

        var inventory = await c.GetJsonAsync<List<JsonElement>>($"{I}/inventory");
        Assert.Equal(3, inventory.Count(x => x.Str("folder") == $"{family}/{pn}"));
    }
}
