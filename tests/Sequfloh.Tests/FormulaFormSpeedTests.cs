using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Sequfloh.Domain.Security;

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
}
