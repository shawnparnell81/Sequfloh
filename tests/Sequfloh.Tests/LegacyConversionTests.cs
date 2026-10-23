using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

/// <summary>
/// Older Office files and web pages saved with an Office name are converted on upload, for imported files and for the parts import, so nobody has to
/// re-save them. The fixtures are synthetic files made with LibreOffice. The conversion tests need the converter installed (it is in the production image).
/// </summary>
[Collection("api")]
public sealed class LegacyConversionTests(TestApp app)
{
    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy", name));
    bool ConverterAvailable() { using var scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<ILegacyOfficeConverter>().Available; }

    static async Task<(HttpStatusCode Status, string Body)> Post(ApiClient c, string url, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        var r = await c.Http.PostAsync(url, form);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Mht_page_is_taken_out_with_its_characters()
    {
        var html = System.Text.Encoding.UTF8.GetString(MhtReader.ToHtml(Fixture("form-mht.doc")));
        Assert.Contains("Example housing \u2013 caf\u00e9", html);
        Assert.Contains("charset=utf-8", html);
    }

    [Fact]
    public async Task Older_and_web_page_files_upload_as_documents_and_spreadsheets()
    {
        if (!ConverterAvailable()) return;
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);
        foreach (var name in new[] { "form.doc", "form-rtf.doc", "form-html.doc", "form-mht.doc" })
        {
            var bytes = Fixture(name);
            var (st, body) = await Post(editor, "/api/imported-files", name, bytes);
            Assert.True(st == HttpStatusCode.OK, $"{name}: {body}");
            var id = JsonDocument.Parse(body).RootElement.Id();
            Assert.Equal(bytes, await StoredOriginal(id)); // kept exactly as uploaded
            var doc = await (await editor.Http.GetAsync($"/api/imported-files/{id}/document")).Content.ReadAsStringAsync();
            Assert.Contains("Example inspection form", doc);
            Assert.Contains("LG-002", doc);
        }
        foreach (var name in new[] { "parts.xls", "parts-html.xls" })
        {
            var (st, body) = await Post(editor, "/api/imported-files", name, Fixture(name));
            Assert.True(st == HttpStatusCode.OK, $"{name}: {body}");
            var id = JsonDocument.Parse(body).RootElement.Id();
            using var scope = app.Services.CreateScope();
            var f = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportedFiles.AsNoTracking().FirstAsync(x => x.Id == id);
            Assert.Equal(Sequfloh.Domain.Imports.ImportedFileKind.Spreadsheet, f.Kind);
        }
    }

    [Fact]
    public async Task Parts_import_reads_older_and_web_page_files()
    {
        if (!ConverterAvailable()) return;
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], site);
        foreach (var (name, first) in new[] { ("parts.xls", "LX-001"), ("parts-html.xls", "LH-001"), ("form.doc", "LG-001"), ("form-mht.doc", "LG-001"), ("form-rtf.doc", "LG-001") })
        {
            var (st, body) = await Post(eng, "/api/sequfloh/parts/import/read", name, Fixture(name));
            Assert.True(st == HttpStatusCode.OK, $"{name}: {body}");
            var t = JsonDocument.Parse(body).RootElement;
            Assert.Equal(0, t.GetProperty("mapping").GetProperty("partNumber").GetInt32());
            Assert.Equal(first, t.GetProperty("rows")[0][0].GetString());
            Assert.Equal(2, t.GetProperty("rows").GetArrayLength());
        }
    }

    [Fact]
    public async Task A_damaged_older_file_gives_a_clear_error()
    {
        if (!ConverterAvailable()) return;
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);
        var junk = Fixture("form.doc")[..600]; // a cut-off file: right signature, no content
        var (st, body) = await Post(editor, "/api/imported-files", "Broken.doc", junk);
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Contains("could not be converted", body);
    }

    async Task<byte[]> StoredOriginal(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var f = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImportedFiles.AsNoTracking().FirstAsync(x => x.Id == id);
        await using var s = (await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(f.OriginalStorageKey, default))!;
        var ms = new MemoryStream(); await s.CopyToAsync(ms); return ms.ToArray();
    }
}
