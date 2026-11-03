using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Sequfloh.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Imports;

namespace Sequfloh.Tests;

/// <summary>A filled form full of formulas opens again quickly, and edits still show the recalculated results.</summary>
[Collection("api")]
public sealed class FormulaFormSpeedTests(TestApp app)
{
    static byte[] Form()
    {
        using var wb = new XLWorkbook();
        for (var s = 1; s <= 4; s++)
        {
            var ws = wb.AddWorksheet($"Check {s}");
            for (var r = 2; r <= 400; r++)
            {
                ws.Cell(r, 1).Value = $"Point {r}"; ws.Cell(r, 4).Value = 25; ws.Cell(r, 5).Value = 30; ws.Cell(r, 6).Value = 20 + r % 15;
                ws.Cell(r, 7).FormulaA1 = $"IF(AND(F{r}>=D{r},F{r}<=E{r}),\"PASS\",\"FAIL\")";
                ws.Cell(r, 8).FormulaA1 = "COUNTIF(G:G,\"FAIL\")";
                ws.Cell(r, 9).FormulaA1 = $"SUM(F$2:F{r})/MAX(1,COUNT(F$2:F{r}))";
            }
            ws.Range("A1:Z1048576").AddConditionalFormat().WhenEquals("\"FAIL\"").Fill.SetBackgroundColor(XLColor.FromHtml("#FFC7CE"));
        }
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    static JsonElement Cell(JsonElement grid, int r, int c) => grid.GetProperty("cells").EnumerateArray().Single(x => x.Int("r") == r && x.Int("c") == c);

    [Fact]
    public async Task Formula_heavy_form_opens_again_quickly_and_previews_still_recalculate()
    {
        var site = await app.CreateSiteAsync();
        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Form()), "file", $"Speed form {Guid.NewGuid():N}"[..20] + ".xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        var id = (await (await user.Http.PostAsync("/api/imported-files", form)).JsonAsync()).Id();

        var first = await user.GetElAsync($"/api/imported-files/{id}/sheet");
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await user.GetElAsync($"/api/imported-files/{id}/sheet");
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"five more opens took {sw.Elapsed}");
        var again = await user.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal(first.GetRawText(), again.GetRawText());
        Assert.Equal("PASS", Cell(again, 10, 7).Str("cached"));
        Assert.Equal("=COUNTIF(G:G,\"FAIL\")", Cell(again, 10, 8).Str("formula"));

        // a typed value still recalculates every formula that depends on it
        var pv = await (await user.Http.PostAsJsonAsync($"/api/imported-files/{id}/preview", new { sheet = "Check 1", edits = new[] { new { sheet = "Check 1", r = 10, c = 6, value = "99" } } })).JsonAsync();
        var values = pv.GetProperty("values").EnumerateArray().ToDictionary(v => (v.Int("r"), v.Int("c")), v => v.Str("value"));
        Assert.Equal("FAIL", values[(10, 7)]);
        var fails = int.Parse(values[(2, 8)]);
        var before = int.Parse(Cell(again, 2, 8).Str("cached"));
        Assert.Equal(before + 1, fails);
    }

    /// <summary>About 60 checks with borders, a colour rule and an entry rule running thousands of rows past the last check, like the injector form.</summary>
    static byte[] LongFormattedForm()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Injector");
        ws.Range("A1:AD6000").Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        for (var r = 5; r <= 64; r++)
        {
            ws.Cell(r, 1).Value = $"Check {r}"; ws.Cell(r, 3).Value = 10; ws.Cell(r, 4).Value = 12; ws.Cell(r, 5).Value = 11;
            ws.Cell(r, 6).FormulaA1 = $"IF(E{r}=\"\",\"\",IF(AND(E{r}>=C{r},E{r}<=D{r}),\"PASS\",\"FAIL\"))";
        }
        for (var r = 67; r <= 6000; r++) ws.Cell(r, 7).FormulaA1 = $"IF(E{r}=\"\",\"\",E{r}*2)";
        ws.Cell(66, 2).Value = "Signed";
        ws.Range("F1:F6000").AddConditionalFormat().WhenEquals("\"FAIL\"").Fill.SetBackgroundColor(XLColor.Red);
        ws.Range("E5:E6000").CreateDataValidation().Decimal.Between(0, 1000);
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    [Fact]
    public void Blank_entry_column_and_a_short_run_of_blank_entry_rows_stay_on_the_form()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Form");
        ws.Cell("A1").Value = "Part No.:";
        ws.Range("A1:C40").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range("A1:C40").Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        var ms = new MemoryStream(); wb.SaveAs(ms);
        var sh = WorkbookReader.Read(new MemoryStream(ms.ToArray()), "xlsx").Sheet("Form")!;
        Assert.Equal(40, sh.LastRow);
        Assert.Equal(3, sh.LastCol);
    }

    [Fact]
    public async Task Form_formatted_far_past_its_last_check_ends_at_the_last_check_and_its_first_open_is_ready_ahead()
    {
        var bytes = LongFormattedForm();
        var data = WorkbookReader.Read(new MemoryStream(bytes), "xlsx");
        Assert.Empty(data.Warnings);
        var sh = data.Sheet("Injector")!;
        Assert.Equal(66, sh.LastRow);
        Assert.Equal(30, sh.LastCol);

        var site = await app.CreateSiteAsync();
        var (_, user) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", $"Injector {Guid.NewGuid():N}"[..20] + ".xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        var id = (await (await user.Http.PostAsync("/api/imported-files", form)).JsonAsync()).Id();
        var grid = await user.GetElAsync($"/api/imported-files/{id}/sheet");
        Assert.Equal(66, grid.Int("r2"));
        Assert.True(grid.GetProperty("cells").GetArrayLength() < 2500);
        Assert.Equal("PASS", Cell(grid, 10, 6).Str("cached"));
        Assert.True(Cell(grid, 10, 5).GetProperty("validation").GetBoolean());
        // an edit still recalculates the check
        var pv = await (await user.Http.PostAsJsonAsync($"/api/imported-files/{id}/preview", new { sheet = "Injector", edits = new[] { new { sheet = "Injector", r = 10, c = 5, value = "50" } } })).JsonAsync();
        Assert.Contains(pv.GetProperty("values").EnumerateArray(), v => v.Int("r") == 10 && v.Int("c") == 6 && v.Str("value") == "FAIL");

        // a form imported before the trim still carries the old note about cut rows; the startup pass writes its summary afresh
        var stale = "{\"warnings\":[\"Injector: only the first 5000 rows are shown in the app; the file keeps every row.\"]}";
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ImportedFiles.IgnoreQueryFilters().Where(f => f.Id == id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Summary, stale));
        }
        await ImportedFileService.PrepareStoredAsync(app.Services, CancellationToken.None);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.DoesNotContain("rows are shown", (await db.ImportedFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == id)).Summary);
            // and opening the form page refreshes one the startup pass has not reached yet
            await db.ImportedFiles.IgnoreQueryFilters().Where(f => f.Id == id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Summary, stale));
        }
        var detail = await user.GetElAsync($"/api/imported-files/{id}");
        Assert.Empty(detail.GetProperty("summary").GetProperty("warnings").EnumerateArray());
        Assert.Equal(66, detail.GetProperty("summary").GetProperty("sheets")[0].Int("rows"));
        Assert.Empty((await user.GetElAsync($"/api/imported-files/{id}")).GetProperty("summary").GetProperty("warnings").EnumerateArray());
    }
}
