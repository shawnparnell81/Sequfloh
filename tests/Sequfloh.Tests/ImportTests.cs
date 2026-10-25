using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

public sealed class CsvParserTests
{
    static MemoryStream S(string text, Encoding? enc = null) { var e = enc ?? new UTF8Encoding(false); return new MemoryStream(e.GetPreamble().Concat(e.GetBytes(text)).ToArray()); }

    [Fact]
    public void Detects_delimiter_header_types_and_reads_quoted_values_with_line_breaks()
    {
        var a = CsvParser.Analyze(S("Part;Qty;Measured;Date;OK\n\"P-1; left\";5;10,25;2026-10-01;yes\n\"P-2 \"\"B\"\"\";7;9,5;2026-10-02;no\n\"P-3\nnote\";1;10;2026-10-03;yes\n"), null);
        Assert.Equal(';', a.Delimiter);
        Assert.Equal(',', a.DecimalSeparator);
        Assert.True(a.HasHeader);
        Assert.Equal(3, a.TotalRows);
        Assert.Equal(["text", "integer", "number", "date", "boolean"], a.Columns.Select(c => c.InferredType));
        Assert.Equal("P-1; left", a.SampleRows[0][0]);
        Assert.Equal("P-2 \"B\"", a.SampleRows[1][0]);
        Assert.Equal("P-3\nnote", a.SampleRows[2][0]);
        Assert.Equal("yyyy-MM-dd", a.DateFormat);
    }

    [Fact]
    public void Headerless_tab_files_utf16_and_latin1_are_read()
    {
        var h = CsvParser.Analyze(S("1\t2\n3\t4\n"), null);
        Assert.Equal('\t', h.Delimiter);
        Assert.False(h.HasHeader);
        Assert.Equal(2, h.TotalRows);
        Assert.Equal("Column 1", h.Columns[0].Name);
        var u = CsvParser.Analyze(S("Name,Size\nÅngström,3\n", new UnicodeEncoding(false, true)), null);
        Assert.Equal("utf-16le", u.Encoding);
        Assert.Equal("Ångström", u.SampleRows[0][0]);
        var l = CsvParser.Analyze(new MemoryStream(Encoding.Latin1.GetBytes("Name,Size\nCafé,3\n")), null);
        Assert.Equal("latin-1", l.Encoding);
        Assert.Equal("Café", l.SampleRows[0][0]);
        var dmy = CsvParser.Analyze(S("When,Value\n25/12/2026,1\n31/01/2026,2\n"), null);
        Assert.Equal("dd/MM/yyyy", dmy.DateFormat);
    }

    [Fact]
    public void Exported_cells_cannot_become_formulas()
    {
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\"", CsvParser.SafeCell("=HYPERLINK(\"x\")"));
        Assert.Equal("\"'+1\"", CsvParser.SafeCell("+1"));
        Assert.Equal("\"'@SUM(A1)\"", CsvParser.SafeCell("@SUM(A1)"));
        Assert.Equal("\"plain\"", CsvParser.SafeCell("plain"));
    }

    [Fact]
    public async Task Security_inspection_checks_content_against_type()
    {
        Assert.NotNull(await ImportSecurity.InspectAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), ".csv", default));
        Assert.NotNull(await ImportSecurity.InspectAsync(new MemoryStream("a,b"u8.ToArray()), ".xlsx", default));
        Assert.NotNull(await ImportSecurity.InspectAsync(new MemoryStream([0x61, 0x00, 0x62]), ".csv", default));
        Assert.Null(await ImportSecurity.InspectAsync(new MemoryStream("a,b\n1,2"u8.ToArray()), ".csv", default));
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true)) { z.CreateEntry("xl/vbaProject.bin"); z.CreateEntry("[Content_Types].xml"); }
        ms.Position = 0;
        Assert.Contains("macros", await ImportSecurity.InspectAsync(ms, ".xlsx", default));
    }

    [Fact]
    public void Inventory_classifies_formulas_and_flags_external_links_dde_and_suspicious_content()
    {
        var wb = new WorkbookData { Format = "xlsm", HasMacros = true };
        var sh = new XlSheet { Name = "S", LastRow = 6, LastCol = 2 };
        void F(int r, string f) => sh.Cells[(r, 2)] = new XlCell(r, 2, "number", null, f, "1", null, true, null, null, null);
        F(1, "SUM(A1:A3)"); F(2, "[Budget.xlsx]Sheet1!A1"); F(3, "TODAY()"); F(4, "CALL(\"kernel32\",\"x\")"); F(5, "cmd|'/c calc'!A0");
        sh.Cells[(6, 1)] = new XlCell(6, 1, "text", "=HYPERLINK(\"http://x\")", null, null, null, true, null, null, "file:///c:/windows/system32/cmd.exe");
        wb.Sheets.Add(sh);
        wb.DdeLinks.Add("cmd|/c calc");
        var a = WorkbookIntake.Analyze(wb);
        Assert.Equal(5, a.FormulaTotal);
        Assert.Contains(FormulaClass.Server, a.Formulas[0].Classes);
        Assert.Contains(FormulaClass.External, a.Formulas[1].Classes);
        Assert.Contains(FormulaClass.Volatile, a.Formulas[2].Classes);
        Assert.Contains(FormulaClass.Macro, a.Formulas[3].Classes);
        Assert.Contains(FormulaClass.External, a.Formulas[4].Classes);
        Assert.Contains(a.Security, s => s.Contains("macro code"));
        Assert.Contains(a.Security, s => s.Contains("DDE"));
        Assert.Contains(a.Security, s => s.Contains("Suspicious hyperlink"));
        Assert.Contains(a.Security, s => s.Contains("formula injection"));
    }

    [Fact]
    public void Word_reader_keeps_styles_lists_tables_sections_header_footer_and_flags_security()
    {
        var m = DocxReader.Read(new MemoryStream(DocxBuilder.FirstArticleReport(macros: true)));
        Assert.Equal("First Article Inspection Report", m.Title);
        var title = m.Blocks.First(b => b.P is not null).P!;
        Assert.Equal(0, title.Heading);
        Assert.Equal("center", title.Align);
        Assert.True(title.Style!.Bold);
        Assert.Equal("#1257C4", title.Style.Colour);
        Assert.True(title.Editable);
        var listed = m.Blocks.Where(b => b.P?.ListLabel is not null).Select(b => b.P!.ListLabel).ToList();
        Assert.Equal(["1.", "1.a)", "2."], listed);
        Assert.Equal(3, m.Tables.Count());
        var parts = m.Tables.First();
        Assert.Equal(4, parts.Rows[0].Cells[0].GridSpan);
        Assert.Equal("#D9E2F3", parts.Rows[0].Cells[0].Shading);
        Assert.Equal("thin", parts.Rows[1].Cells[0].BorderTop);
        Assert.True(m.Tables.ElementAt(1).Rows[0].Header);
        Assert.Equal("restart", m.Tables.ElementAt(2).Rows[0].Cells[0].VMerge);
        Assert.Equal(28, m.Tables.ElementAt(2).Rows[0].Height);
        Assert.Contains(m.Blocks, b => b.Kind == "section" && b.Section!.Orientation == "portrait");
        Assert.Equal("landscape", m.FinalSection.Orientation);
        Assert.Contains(m.Footer[0].Segs, s => s.Kind == "page");
        Assert.Null(m.Footer[0].Ref);
        Assert.Contains(m.Controls, c => c.Type == "dropdown" && c.Items.Contains("Design change"));
        Assert.Contains(m.Controls, c => c.Type == "checkbox" && c.Legacy && c.Tag == "CertAttached");
        Assert.Equal(1, m.Images);
        Assert.True(m.HasMacros);
        Assert.Contains(m.Security, s => s.Contains("javascript:"));
        Assert.Contains("Calibri", m.Fonts);
    }

    [Fact]
    public void Word_edits_keep_the_paragraph_and_run_formatting()
    {
        var bytes = DocxBuilder.FirstArticleReport();
        var m = DocxReader.Read(new MemoryStream(bytes));
        var highlighted = m.Blocks.First(b => b.P?.Text.StartsWith("Highlighted") == true).P!;
        var saved = DocxEditor.Apply(bytes, new Dictionary<int, string> { [highlighted.Ref!.Value] = "Critical characteristics are marked CC.\nCheck them twice." });
        var again = DocxReader.Read(new MemoryStream(saved));
        var p = again.Blocks.Single(b => b.P?.Ref == highlighted.Ref).P!;
        Assert.Equal("Critical characteristics are marked CC.Check them twice.", p.Text);
        Assert.Contains(p.Segs, s => s.Kind == "break");
        Assert.True(p.Segs.First().Style!.Bold);
        Assert.Equal("yellow", p.Segs.First().Style!.Highlight);
        Assert.Equal(m.Tables.Count(), again.Tables.Count());
        Assert.Equal("landscape", again.FinalSection.Orientation);
    }

    [Fact]
    public void Formulas_with_no_stored_result_are_worked_out_on_import()
    {
        using var src = new XLWorkbook();
        var ws = src.AddWorksheet("Audit");
        ws.Cell(1, 1).Value = 24; ws.Cell(1, 2).Value = 28; ws.Cell(2, 1).Value = 29.4;
        ws.Cell(2, 2).FormulaA1 = "IF(AND(A2>=A1,A2<=B1),\"PASS\",\"FAIL\")";
        var ms = new MemoryStream();
        src.SaveAs(ms, new SaveOptions { EvaluateFormulasBeforeSaving = false });
        var sh = WorkbookReader.Read(new MemoryStream(ms.ToArray()), "xlsx").Sheet("Audit")!;
        Assert.Equal("FAIL", sh.At(2, 2)!.Cached);
    }

    [Fact]
    public void Cell_edits_keep_formulas_merges_and_formatting_and_recalculate()
    {
        var (saved, recalculated) = ImportedFileService.ApplyCells(ImportTests.InspectionWorkbook(), [new CellEdit("Checks", 3, 2, "12.5"), new CellEdit("Checks", 6, 3, "=B5*2")]);
        Assert.True(recalculated);
        var wb = WorkbookReader.Read(new MemoryStream(saved), "xlsx");
        var sh = wb.Sheet("Checks")!;
        Assert.Equal("SUM(B3:B4)", sh.At(5, 2)!.Formula);
        Assert.Equal("32.5", sh.At(5, 2)!.Cached);
        Assert.Equal("B5*2", sh.At(6, 3)!.Formula);
        Assert.Equal("65", sh.At(6, 3)!.Cached);
        Assert.Contains((1, 1, 1, 3), sh.Merges);
        Assert.True(sh.At(1, 1)!.Style!.Bold);
        Assert.Equal("#D9E2F3", sh.At(1, 1)!.Style!.Fill);
        Assert.Equal("0.00", sh.At(3, 2)!.NumberFormat);
    }
}

[Collection("api")]
public sealed class ImportTests(TestApp app)
{
    /// <summary>A small inspection workbook written for these tests: a merged, filled title, typed values with a number format, totals and a second sheet.</summary>
    public static byte[] InspectionWorkbook()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Checks");
        ws.Cell(1, 1).Value = "Torque checks";
        ws.Range(1, 1, 1, 3).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E2F3");
        ws.Cell(2, 1).Value = "Bolt"; ws.Cell(2, 2).Value = "Measured"; ws.Cell(2, 3).Value = "Limit";
        ws.Cell(3, 1).Value = "M6"; ws.Cell(3, 2).Value = 10.0; ws.Cell(3, 3).Value = 12.0;
        ws.Cell(4, 1).Value = "M8"; ws.Cell(4, 2).Value = 20.0; ws.Cell(4, 3).Value = 25.0;
        ws.Range(3, 2, 4, 3).Style.NumberFormat.Format = "0.00";
        ws.Cell(5, 1).Value = "Total"; ws.Cell(5, 2).FormulaA1 = "SUM(B3:B4)"; ws.Cell(5, 3).FormulaA1 = "SUM(C3:C4)";
        ws.Cell(6, 1).Value = "Within limit"; ws.Cell(6, 2).FormulaA1 = "IF(B5<=C5,\"Yes\",\"No\")";
        ws.Column(1).Width = 18;
        wb.AddWorksheet("Notes").Cell(1, 1).Value = "Second sheet";
        wb.RecalculateAllFormulas();
        var ms = new MemoryStream();
        wb.SaveAs(ms, new SaveOptions { EvaluateFormulasBeforeSaving = true });
        return ms.ToArray();
    }

    static async Task<JsonElement> UploadAsync(ApiClient c, string fileName, byte[] bytes, string? title = null, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        if (title is not null) form.Add(new StringContent(title), "title");
        var r = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(r.StatusCode == expect, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.IsSuccessStatusCode ? await r.JsonAsync() : default;
    }

    static JsonElement CellAt(JsonElement grid, int r, int c) => grid.GetProperty("cells").EnumerateArray().Single(x => x.Int("r") == r && x.Int("c") == c);

    [Fact]
    public async Task Excel_file_imports_as_it_is_and_cell_edits_are_saved_as_a_new_version_with_formulas_recalculated()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles], site);
        var (_, outsider) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles], await app.CreateSiteAsync());

        var savedName = $"Torque checks {Guid.NewGuid():N}"[..24];
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(InspectionWorkbook()), "file", savedName + ".xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        var upload = new HttpRequestMessage(HttpMethod.Post, "/api/imported-files") { Content = form };
        upload.Headers.Add("X-Test-Own-Number", "1");
        var created = await (await editor.Http.SendAsync(upload)).JsonAsync();
        var id = created.Id();
        Assert.Equal(savedName, created.Str("number")); // the file name as saved, without its extension
        Assert.Equal(savedName, created.Str("title"));

        var detail = await viewer.GetElAsync($"/api/imported-files/{id}");
        Assert.Equal("spreadsheet", detail.Str("kind"));
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        Assert.Equal(3, detail.GetProperty("summary").GetProperty("formulaTotal").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/imported-files/{id}")).StatusCode);

        var grid = await viewer.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal("Checks", grid.Str("sheet"));
        Assert.Equal(["Checks", "Notes"], grid.GetProperty("sheets").EnumerateArray().Select(s => s.GetString()));
        var title = CellAt(grid, 1, 1);
        Assert.Equal(3, title.Int("colSpan"));
        Assert.True(title.GetProperty("style").GetProperty("bold").GetBoolean());
        var total = CellAt(grid, 5, 2);
        Assert.Equal("=SUM(B3:B4)", total.Str("formula"));
        Assert.Equal("30", total.Str("cached"));

        // a viewer cannot save, a stale version is refused
        var edit = new { baseVersion = 1, edits = new[] { new { sheet = "Checks", r = 3, c = 2, value = "12.5" }, new { sheet = "Checks", r = 7, c = 2, value = "=B5/2" } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"/api/imported-files/{id}/cells", edit)).StatusCode);
        var saved = await (await editor.PutAsync($"/api/imported-files/{id}/cells", edit)).JsonAsync();
        Assert.Equal(2, saved.Int("version"));
        Assert.Equal(HttpStatusCode.Conflict, (await editor.PutAsync($"/api/imported-files/{id}/cells", edit)).StatusCode);

        grid = await viewer.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal("32.5", CellAt(grid, 5, 2).Str("cached"));
        Assert.Equal("16.25", CellAt(grid, 7, 2).Str("cached"));
        Assert.Equal("=B5/2", CellAt(grid, 7, 2).Str("formula"));
        var first = await viewer.GetElAsync($"/api/imported-files/{id}/sheet?version=1");
        Assert.Equal("30", CellAt(first, 5, 2).Str("cached"));

        // the real file downloads with the formula and merge intact
        var file = await (await viewer.GetAsync($"/api/imported-files/{id}/download")).Content.ReadAsByteArrayAsync();
        using (var wb = new XLWorkbook(new MemoryStream(file)))
        {
            Assert.Equal("SUM(B3:B4)", wb.Worksheet("Checks").Cell(5, 2).FormulaA1);
            Assert.True(wb.Worksheet("Checks").Cell(1, 1).IsMerged());
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.Where(a => a.EntityId == id).OrderBy(a => a.Id).ToListAsync();
        Assert.Contains(audit, a => a.Action == "Created" && a.UserName.StartsWith("Test"));
        var edited = audit.Single(a => a.Action == "Edited");
        Assert.Contains("version 2", edited.Description);
        Assert.Contains("Checks!B3, B7", edited.Description);
        Assert.False(string.IsNullOrEmpty(edited.UserName));
        Assert.Equal(2, await db.ImportedFileVersions.CountAsync(v => v.ImportedFileId == id));
    }

    [Fact]
    public async Task Blank_forms_stay_blank_and_filled_copies_and_saved_copies_are_linked_and_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles], site);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(InspectionWorkbook()), "file", "Torque checks.xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        form.Add(new StringContent("true"), "blankForm");
        var blankId = (await (await editor.Http.PostAsync("/api/imported-files", form)).JsonAsync()).Id();
        var blank = await editor.GetElAsync($"/api/imported-files/{blankId}");
        Assert.True(blank.GetProperty("isBlankForm").GetBoolean());

        // protected unless Edit blank is on
        var edit = new { baseVersion = 1, edits = new[] { new { sheet = "Checks", r = 3, c = 2, value = "11" } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PutAsync($"/api/imported-files/{blankId}/cells", edit)).StatusCode);
        Assert.Equal(2, (await (await editor.PutAsync($"/api/imported-files/{blankId}/cells", new { edit.baseVersion, edit.edits, editBlank = true })).JsonAsync()).Int("version"));

        // a viewer cannot fill out; an editor gets a new file from the current blank version
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/imported-files/{blankId}/fill-out", new { title = "x" })).StatusCode);
        var filled = await (await editor.PostAsync($"/api/imported-files/{blankId}/fill-out", new { title = "Torque checks – line 2" })).JsonAsync();
        var filledId = filled.Id();
        var fd = await editor.GetElAsync($"/api/imported-files/{filledId}");
        Assert.False(fd.GetProperty("isBlankForm").GetBoolean());
        Assert.Equal(blankId, fd.GetProperty("blankForm").Id());
        Assert.Equal(2, fd.GetProperty("blankForm").Int("version"));
        Assert.Equal("Torque checks – line 2", fd.Str("title"));
        var grid = await editor.GetElAsync($"/api/imported-files/{filledId}/sheet");
        Assert.Equal("31", CellAt(grid, 5, 2).Str("cached"));

        // the copy edits normally and the blank does not change
        var saved = await editor.PutAsync($"/api/imported-files/{filledId}/cells", new { baseVersion = 1, edits = new[] { new { sheet = "Checks", r = 4, c = 2, value = "30" } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("31", CellAt(await editor.GetElAsync($"/api/imported-files/{blankId}/sheet"), 5, 2).Str("cached"));

        // save as new copy from the filled copy: the filled copy stays as it is, the new one links to the same blank
        var copy = await (await editor.PostAsync($"/api/imported-files/{filledId}/save-as-copy", new { baseVersion = 2, title = "Retest", cells = new[] { new { sheet = "Checks", r = 3, c = 2, value = "1" } } })).JsonAsync();
        Assert.Equal("41", CellAt(await editor.GetElAsync($"/api/imported-files/{filledId}/sheet"), 5, 2).Str("cached"));
        Assert.Equal("31", CellAt(await editor.GetElAsync($"/api/imported-files/{copy.Id()}/sheet"), 5, 2).Str("cached"));
        Assert.Equal(blankId, (await editor.GetElAsync($"/api/imported-files/{copy.Id()}")).GetProperty("blankForm").Id());

        // updating the blank with a new upload leaves copies alone
        var up = new MultipartFormDataContent();
        up.Add(new ByteArrayContent(InspectionWorkbook()), "file", "Torque checks v3.xlsx");
        Assert.Equal(3, (await (await editor.Http.PostAsync($"/api/imported-files/{blankId}/versions", up)).JsonAsync()).Int("version"));
        Assert.Equal("30", CellAt(await editor.GetElAsync($"/api/imported-files/{blankId}/sheet"), 5, 2).Str("cached"));
        Assert.Equal("41", CellAt(await editor.GetElAsync($"/api/imported-files/{filledId}/sheet"), 5, 2).Str("cached"));

        // the blank lists its copies newest first; list filters
        var copies = (await editor.GetElAsync($"/api/imported-files/{blankId}")).GetProperty("copies").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Equal([copy.Id(), filledId], copies);
        var blanks = (await editor.GetElAsync("/api/imported-files?kind=blank")).GetProperty("items").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Contains(blankId, blanks); Assert.DoesNotContain(filledId, blanks);
        var filledList = (await editor.GetElAsync("/api/imported-files?kind=filled")).GetProperty("items").EnumerateArray().Select(c => c.Id()).ToList();
        Assert.Contains(filledId, filledList); Assert.Contains(copy.Id(), filledList); Assert.DoesNotContain(blankId, filledList);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fromBlank = await db.AuditEntries.Where(a => a.Action == "Created from blank form" && a.EntityId == filledId).SingleAsync();
        Assert.Contains("from blank form", fromBlank.Description);
        Assert.False(string.IsNullOrEmpty(fromBlank.UserName));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "Created from blank form" && a.EntityId == blankId));
        var asCopy = await db.AuditEntries.Where(a => a.Action == "Saved as copy" && a.EntityId == copy.Id()).SingleAsync();
        Assert.Contains("was left as it is", asCopy.Description);
        Assert.Contains("Checks!B3", asCopy.Description);
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "Saved as copy" && a.EntityId == blankId));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "Saved as copy" && a.EntityId == filledId));
    }

    [Fact]
    public async Task Csv_becomes_a_workbook_with_typed_cells_and_formula_like_text_stays_text()
    {
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);
        var csv = Encoding.UTF8.GetBytes("Gauge;Reading;Note\nG-1;10,5;ok\nG-2;11;=1+1\n");
        var id = (await UploadAsync(editor, "gauges.csv", csv)).Id();
        var grid = await editor.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal("gauges", grid.Str("sheet"));
        Assert.Equal("10.5", CellAt(grid, 2, 2).Str("text"));
        var note = CellAt(grid, 3, 3);
        Assert.Equal("=1+1", note.Str("text"));
        Assert.Equal(JsonValueKind.Null, note.GetProperty("formula").ValueKind);
        var original = await (await editor.GetAsync($"/api/imported-files/{id}/download?original=true")).Content.ReadAsByteArrayAsync();
        Assert.Equal(csv, original);
        await UploadAsync(editor, "fake.xlsx", csv, expect: HttpStatusCode.BadRequest);
        await UploadAsync(editor, "notes.txt", csv, expect: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Word_file_imports_and_paragraph_edits_are_saved_while_fields_and_controls_stay_as_they_are()
    {
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);
        var id = (await UploadAsync(editor, "FAI report.docx", DocxBuilder.FirstArticleReport(), "First article report")).Id();
        var doc = await editor.GetElAsync($"/api/imported-files/{id}/document");
        var paras = doc.GetProperty("blocks").EnumerateArray().Where(b => b.GetProperty("p").ValueKind == JsonValueKind.Object).Select(b => b.GetProperty("p")).ToList();
        var instr = paras.First(p => p.GetProperty("segs").EnumerateArray().Any(s => s.Str("text").StartsWith("Measure every")));
        var withControl = paras.First(p => !p.GetProperty("editable").GetBoolean() && p.GetProperty("ref").ValueKind == JsonValueKind.Number);
        Assert.Equal("1.", instr.Str("listLabel"));

        var bad = new { baseVersion = 1, edits = new[] { new { @ref = withControl.Int("ref"), text = "x" } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PutAsync($"/api/imported-files/{id}/document", bad)).StatusCode);
        var ok = new { baseVersion = 1, edits = new[] { new { @ref = instr.Int("ref"), text = "Measure every characteristic, including notes." } } };
        Assert.Equal(2, (await (await editor.PutAsync($"/api/imported-files/{id}/document", ok)).JsonAsync()).Int("version"));

        doc = await editor.GetElAsync($"/api/imported-files/{id}/document");
        var again = doc.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("p")).Where(p => p.ValueKind == JsonValueKind.Object).Single(p => p.Int("ref") == instr.Int("ref"));
        Assert.Equal("Measure every characteristic, including notes.", again.GetProperty("segs")[0].Str("text"));
        Assert.Equal("1.", again.Str("listLabel"));
        var file = await (await editor.GetAsync($"/api/imported-files/{id}/download")).Content.ReadAsByteArrayAsync();
        var model = DocxReader.Read(new MemoryStream(file));
        Assert.Contains(model.Controls, c => c.Type == "dropdown");
        Assert.Equal(3, model.Tables.Count());
    }

    [Fact]
    public async Task Legacy_xls_goes_through_the_office_converter()
    {
        using var scope = app.Services.CreateScope();
        var legacy = scope.ServiceProvider.GetRequiredService<ILegacyOfficeConverter>();
        if (!legacy.Available) return; // the converter is optional; without it the upload reports that the format needs it
        var xls = await legacy.ConvertAsync(new MemoryStream(InspectionWorkbook()), "xlsx", "xls", default);
        var bytes = ((MemoryStream)xls).ToArray();
        Assert.Equal(0xD0, bytes[0]);
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles]);
        var id = (await UploadAsync(editor, "torque.xls", bytes)).Id();
        var grid = await editor.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal("=SUM(B3:B4)", CellAt(grid, 5, 2).Str("formula"));
        Assert.Equal("30", CellAt(grid, 5, 2).Str("cached"));
    }
}
