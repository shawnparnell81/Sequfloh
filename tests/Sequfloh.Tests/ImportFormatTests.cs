using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

/// <summary>Files named .doc are handled by what they really contain. Synthetic files only.</summary>
[Collection("api")]
public sealed class ImportFormatTests(TestApp app)
{
    static readonly string Rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\\fs22 Example test procedure\\par Step 1: check the part.\\par}";
    static readonly string Html = "<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:w=\"urn:schemas-microsoft-com:office:word\"><head><meta name=Generator content=\"Microsoft Word 15\"></head><body><h1>Example test procedure</h1><p>Step 1: check the part.</p></body></html>";
    static readonly string Mht = "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"----=_NextPart\"\r\n\r\nThis document is a Single File Web Page, also known as a Web Archive file.\r\n";

    async Task<(HttpStatusCode Status, string Body)> Upload(ApiClient c, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        var r = await c.Http.PostAsync("/api/imported-files", form);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    async Task<byte[]> StoredOriginal(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var f = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportedFiles.AsNoTracking().FirstAsync(x => x.Id == id);
        await using var s = (await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(f.OriginalStorageKey, default))!;
        var ms = new MemoryStream(); await s.CopyToAsync(ms); return ms.ToArray();
    }

    bool ConverterAvailable() { using var scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<ILegacyOfficeConverter>().Available; }

    [Fact]
    public async Task Content_is_detected_regardless_of_the_extension()
    {
        Assert.Equal("docx", await ImportSecurity.DetectAsync(new MemoryStream(DocxBuilder.FirstArticleReport()), default));
        Assert.Equal("rtf", await ImportSecurity.DetectAsync(new MemoryStream(Encoding.ASCII.GetBytes(Rtf)), default));
        Assert.Equal("html", await ImportSecurity.DetectAsync(new MemoryStream(Encoding.UTF8.GetBytes(Html)), default));
        Assert.Equal("mht", await ImportSecurity.DetectAsync(new MemoryStream(Encoding.ASCII.GetBytes(Mht)), default));
        Assert.Equal("ole", await ImportSecurity.DetectAsync(new MemoryStream([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]), default));
        Assert.Null(await ImportSecurity.DetectAsync(new MemoryStream(Enumerable.Range(0, 500).Select(i => (byte)(i * 37 % 251 + 1)).ToArray()), default));
        Assert.Equal("Example Procedure - Rev C", ImportedFileService.DefaultTitle("Example Procedure - Rev C.doc.doc"));
        Assert.Equal("Plain", ImportedFileService.DefaultTitle("Plain.docx"));
    }

    [Fact]
    public async Task Misnamed_word_files_import_and_are_kept_as_uploaded()
    {
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);

        var docx = DocxBuilder.FirstArticleReport();
        var (st, body) = await Upload(editor, "Example Procedure - Rev C.doc.doc", docx);
        Assert.True(st == HttpStatusCode.OK, body);
        var el = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.Equal("Example Procedure - Rev C", el.GetProperty("title").GetString());
        Assert.Equal(docx, await StoredOriginal(el.Id()));
        Assert.Equal(HttpStatusCode.OK, (await editor.Http.GetAsync($"/api/imported-files/{el.Id()}/document")).StatusCode);

        var random = Enumerable.Range(0, 500).Select(i => (byte)(i * 37 % 251 + 1)).ToArray();
        (st, body) = await Upload(editor, "Random.doc", random);
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Contains("not a legacy Office document", body);

        (st, body) = await Upload(editor, "Archive.doc", Encoding.ASCII.GetBytes(Mht));
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Contains("has no page content", body); // a web archive without a page is a clear error, not a request to re-save

        if (!ConverterAvailable())
        {
            (st, body) = await Upload(editor, "Example.doc", Encoding.ASCII.GetBytes(Rtf));
            Assert.Equal(HttpStatusCode.BadRequest, st);
            Assert.Contains("cannot be converted on this server", body);
            return;
        }
        foreach (var (name, bytes) in new[] { ("Example RTF.doc", Encoding.ASCII.GetBytes(Rtf)), ("Example web page.doc", Encoding.UTF8.GetBytes(Html)) })
        {
            (st, body) = await Upload(editor, name, bytes);
            Assert.True(st == HttpStatusCode.OK, body);
            var id = System.Text.Json.JsonDocument.Parse(body).RootElement.Id();
            Assert.Equal(bytes, await StoredOriginal(id));
            Assert.Contains("Example test procedure", await (await editor.Http.GetAsync($"/api/imported-files/{id}/document")).Content.ReadAsStringAsync());
        }
        using var scope = app.Services.CreateScope();
        var ole = ((MemoryStream)await scope.ServiceProvider.GetRequiredService<ILegacyOfficeConverter>().ConvertAsync(new MemoryStream(docx), "docx", "doc", default)).ToArray();
        (st, body) = await Upload(editor, "Real legacy.doc", ole);
        Assert.True(st == HttpStatusCode.OK, body);
        Assert.Equal(ole, await StoredOriginal(System.Text.Json.JsonDocument.Parse(body).RootElement.Id()));
    }
}
