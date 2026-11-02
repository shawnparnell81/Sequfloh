using System.Diagnostics;
using ClosedXML.Excel;
using Sequfloh.Imports;

namespace Sequfloh.Tests;

/// <summary>Filled forms whose conditional formatting covers whole columns open quickly and keep every value on every sheet.</summary>
public sealed class WholeColumnFormattingTests
{
    static byte[] Form()
    {
        using var wb = new XLWorkbook();
        for (var s = 1; s <= 4; s++)
        {
            var ws = wb.AddWorksheet($"Check {s}");
            for (var r = 2; r <= 300; r++)
            {
                ws.Cell(r, 1).Value = $"Point {r}"; ws.Cell(r, 4).Value = 25; ws.Cell(r, 5).Value = 30; ws.Cell(r, 6).Value = 20 + r % 15;
                ws.Cell(r, 7).FormulaA1 = $"IF(AND(F{r}>=D{r},F{r}<=E{r}),\"PASS\",\"FAIL\")";
            }
            var all = ws.Range("A1:Z1048576");
            all.AddConditionalFormat().WhenEquals("\"FAIL\"").Fill.SetBackgroundColor(XLColor.FromHtml("#FFC7CE"));
            all.AddConditionalFormat().WhenContains("FAIL").Font.SetBold();
        }
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    [Fact]
    public void Whole_column_rules_do_not_slow_the_read_or_drop_cells()
    {
        var bytes = Form();
        var sw = Stopwatch.StartNew();
        var data = WorkbookReader.Read(new MemoryStream(bytes), "xlsx");
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"read took {sw.Elapsed}");
        Assert.DoesNotContain(data.Warnings, w => w.StartsWith("Only the first"));
        var last = data.Sheet("Check 4")!;
        Assert.Equal("Point 300", last.Cells[(300, 1)].Text);
        Assert.NotNull(last.Cells[(300, 7)].Formula);
        Assert.Equal(2, last.Conditionals.Count);
        var grid = WorkbookIntake.Grid(data, "Check 4", 500, 60);
        Assert.Contains(grid.Cells, c => c.R == 300 && c.C == 1);
    }
}
