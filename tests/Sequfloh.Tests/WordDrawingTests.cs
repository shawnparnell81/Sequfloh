using System.IO.Compression;
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
public sealed class WordDrawingTests(TestApp app)
{
    const string D = "/api/drawings";
    const string I = "/api/drawings/import";
    static readonly string[] Manager = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];
    const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>A minimal made-up .docx: a ZIP with [Content_Types].xml and word/document.xml.</summary>
    internal static byte[] WordDocx(string text = "Made-up assembly note")
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string body) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(body); }
            Add("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("word/document.xml", $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    /// <summary>A ZIP that is not a Word document (no word/document.xml).</summary>
    static byte[] PlainZip()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using var w = new StreamWriter(zip.CreateEntry("notes.txt").Open()); w.Write("made-up"); }
        return ms.ToArray();
    }

    /// <summary>An OLE2 header followed by filler; only the signature is checked for .doc.</summary>
    internal static byte[] WordDoc() => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[504]];

    static MultipartFormDataContent Form(byte[] bytes, string fileName, string title)
    {
        var f = new MultipartFormDataContent();
        f.Add(new ByteArrayContent(bytes), "file", fileName);
        f.Add(new StringContent(title), "title");
        f.Add(new StringContent("A"), "revision");
        return f;
    }

    [Fact]
    public async Task Word_files_upload_into_a_folder_and_download_and_fakes_are_turned_away()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        var folder = (await (await c.PostOkAsync($"{D}/folders", new { name = "Word " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();

        var docx = WordDocx();
        var r = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(docx, "assembly-note.docx", "Assembly note"));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var id = (await r.JsonAsync()).Id();
        var file = await c.GetAsync($"{D}/{id}/file");
        Assert.Equal(Docx, file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(docx, await file.Content.ReadAsByteArrayAsync());
        var view = await c.GetElAsync($"{D}/{id}");
        Assert.All(view.GetProperty("revisions").EnumerateArray(), v => Assert.False(v.GetProperty("viewable").GetBoolean()));
        // the uploader's name is not sent to the drawing page or lists; it stays in the history
        Assert.All(view.GetProperty("revisions").EnumerateArray(), v => Assert.False(v.TryGetProperty("createdByName", out _)));
        var list = (await c.GetElAsync($"{D}/folders?parentId={folder}")).GetRawText();
        Assert.DoesNotContain("createdByName", list);

        r = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(WordDoc(), "old-note.doc", "Old note"));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("application/msword", (await c.GetAsync($"{D}/{(await r.JsonAsync()).Id()}/file")).Content.Headers.ContentType?.MediaType);

        foreach (var (bytes, name) in new[] { (Encoding.ASCII.GetBytes("%PDF-1.4 made-up"), "renamed.docx"), (PlainZip(), "zip-renamed.docx"), (WordDocx(), "wrong.doc") })
        {
            var bad = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(bytes, name, "Fake"));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("does not look like", await bad.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_folder_import_takes_Word_files_links_them_by_name_and_still_refuses_other_types()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Manager, site);
        string part;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Part { PartNumber = "WD-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Test cover", SiteId = site, Status = MasterStatus.Active };
            db.Parts.Add(p); await db.SaveChangesAsync(); part = p.PartNumber;
        }
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "WImport " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();
        var docx = WordDocx();
        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = true, files = new[] { new { path = $"{part}.docx", size = docx.LongLength } }, folders = Array.Empty<string>() })).JsonAsync();
        Assert.Equal([part], plan.GetProperty("files").EnumerateArray().Single().GetProperty("parts").EnumerateArray().Select(x => x.GetString()));

        var s = await (await c.PostOkAsync($"{I}/start", new { folderId = root, fileName = $"{part}.docx", size = docx.LongLength })).JsonAsync();
        Assert.Equal(Docx, s.Str("contentType"));
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(docx))).StatusCode);
        var done = await (await c.PostOkAsync($"{I}/finish", new { token = s.Str("token"), revision = "A", linkParts = true, matchNames = Array.Empty<string>() })).JsonAsync();
        Assert.Equal(1, done.Int("linked"));
        Assert.Contains((await c.GetJsonAsync<List<JsonElement>>($"{D}/for-part?partNumber={part}")), d => d.Str("fileName") == $"{part}.docx");

        // a fake .docx is removed at finish; a text file is refused at start
        var fake = Encoding.ASCII.GetBytes("plain text, not a Word file");
        s = await (await c.PostOkAsync($"{I}/start", new { folderId = root, fileName = "fake.docx", size = fake.LongLength })).JsonAsync();
        await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(fake));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/finish", new { token = s.Str("token"), revision = "A", linkParts = true, matchNames = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/start", new { folderId = root, fileName = "readme.txt", size = 10L })).StatusCode);
    }
}
