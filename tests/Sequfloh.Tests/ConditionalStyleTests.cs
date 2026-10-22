using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Sequfloh.Imports;

namespace Sequfloh.Tests;

/// <summary>Conditional formatting on imported workbooks is worked out from current values, so colours follow edits.</summary>
public sealed class ConditionalStyleTests
{
    static byte[] Form()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Torque Audit");
        ws.Cell("D7").Value = "Min"; ws.Cell("E7").Value = "Max"; ws.Cell("F7").Value = "Reading"; ws.Cell("G7").Value = "Result";
        for (var r = 8; r <= 9; r++)
        {
            ws.Cell(r, 4).Value = 25; ws.Cell(r, 5).Value = 30;
            ws.Cell(r, 7).FormulaA1 = $"IF(AND(F{r}>=D{r},F{r}<=E{r}),\"PASS\",\"FAIL\")";
        }
        ws.Cell("F8").Value = 20; ws.Cell("F9").Value = 27;
        var g = ws.Range("G8:G9");
        g.AddConditionalFormat().WhenEquals("\"PASS\"").Fill.SetBackgroundColor(XLColor.FromHtml("#C6EFCE"));
        g.AddConditionalFormat().WhenEquals("\"FAIL\"").Fill.SetBackgroundColor(XLColor.FromHtml("#FFC7CE"));
        ws.Range("F8:F9").AddConditionalFormat().WhenIsTrue("OR($F8<$D8,$F8>$E8)").Font.SetFontColor(XLColor.FromHtml("#9C0006"));
        var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    static Dictionary<string, ConditionalLook> Looks(byte[] bytes)
    {
        var data = WorkbookReader.Read(new MemoryStream(bytes), "xlsx");
        var sh = data.Sheet("Torque Audit")!;
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        return ConditionalStyles.Evaluate(wb, sh, 1, 1, 20, 10).ToDictionary(l => $"{(char)('A' + l.C - 1)}{l.R}");
    }

    [Fact]
    public void Pass_and_fail_colours_follow_the_values_and_change_after_an_edit()
    {
        var bytes = Form();
        var before = Looks(bytes);
        Assert.Equal("#FFC7CE", before["G8"].Fill);
        Assert.Equal("#C6EFCE", before["G9"].Fill);
        Assert.Equal("#9C0006", before["F8"].Colour);
        Assert.False(before.ContainsKey("F9"));

        var (edited, recalculated) = ImportedFileService.ApplyCells(bytes, [new CellEdit("Torque Audit", 8, 6, "26.1"), new CellEdit("Torque Audit", 9, 6, "31")]);
        Assert.True(recalculated);
        var after = Looks(edited);
        Assert.Equal("#C6EFCE", after["G8"].Fill);
        Assert.Equal("#FFC7CE", after["G9"].Fill);
        Assert.False(after.ContainsKey("F8"));
        Assert.Equal("#9C0006", after["F9"].Colour);
    }

    /// <summary>Files written by other tools: colours with a 00 alpha byte and formulas with no stored results.</summary>
    static byte[] AsOtherTool(byte[] bytes)
    {
        var ms = new MemoryStream(); ms.Write(bytes);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, true))
            foreach (var name in new[] { "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            {
                var e = zip.GetEntry(name)!;
                string xml; using (var r = new StreamReader(e.Open())) xml = r.ReadToEnd();
                xml = name.Contains("styles") ? Regex.Replace(xml, "rgb=\"FF([0-9A-F]{6})\"", "rgb=\"00$1\"") : Regex.Replace(xml, "(</f>)<v>[^<]*</v>", "$1");
                e.Delete();
                using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(xml);
            }
        return ms.ToArray();
    }

    [Fact]
    public void Colours_show_for_files_from_other_tools_without_stored_results()
    {
        var bytes = AsOtherTool(Form());
        var sh = WorkbookReader.Read(new MemoryStream(bytes), "xlsx").Sheet("Torque Audit")!;
        var looks = ImportedFileService.Looks(bytes, null, sh, 1, 1, 20, 10).ToDictionary(l => (l.R, l.C));
        Assert.Equal("#FFC7CE", looks[(8, 7)].Fill);
        Assert.Equal("#C6EFCE", looks[(9, 7)].Fill);
        var edited = ImportedFileService.Looks(bytes, [new CellEdit("Torque Audit", 8, 6, "28")], sh, 1, 1, 20, 10).ToDictionary(l => (l.R, l.C));
        Assert.Equal("#C6EFCE", edited[(8, 7)].Fill);
    }

    [Theory]
    [InlineData("$F8<$D8", 1, 0, "$F9<$D9")]
    [InlineData("F8>=D$8", 2, 1, "G10>=E$8")]
    [InlineData("A1=\"B2\"", 1, 1, "B2=\"B2\"")]
    [InlineData("LOG10(A1)>1", 0, 1, "LOG10(B1)>1")]
    public void Rule_formulas_move_with_each_cell(string formula, int dr, int dc, string expected) => Assert.Equal(expected, ConditionalStyles.Shift(formula, dr, dc));
}
