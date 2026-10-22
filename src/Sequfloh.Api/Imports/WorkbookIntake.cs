using Sequfloh.Application.Abstractions;
using System.Globalization;

namespace Sequfloh.Imports;

public sealed record SheetInfo(string Name, int Index, string Visibility, int Rows, int Cols, int Cells, int Formulas, int Validations, int Conditionals, int Tables, int Merges,
    int Images, int Charts, int Pivots, int Comments, int Hyperlinks, bool Protected, string? PrintArea, string Orientation, int FreezeRows, int FreezeCols, string? AutoFilter);
public sealed record WorkbookSummary(string Format, string CalcMode, bool HasMacros, List<SheetInfo> Sheets, List<XlName> Names, List<string> ExternalLinks, List<string> DdeLinks,
    List<string> Warnings, List<string> Fonts, Dictionary<string, int> FormulaClasses, int FormulaTotal, List<FormulaInfo> Formulas, List<string> Security);
public sealed record GridCell(int R, int C, string? Text, string? Formula, string? Cached, bool Validation, string? Name, int RowSpan, int ColSpan, CellStyle? Style, bool Locked, string? Comment);
public sealed record GridPreview(string Sheet, int R1, int C1, int R2, int C2, List<double> Widths, List<double> Heights, List<GridCell> Cells, bool Truncated, List<string> Sheets);
public sealed record SheetTable(string Sheet, int HeaderRow, List<CsvColumn> Columns, List<(int Row, string[] Cells)> Rows);

/// <summary>
/// Workbook summary for imported files: the sheet list and formula inventory with formula classes and security flags, and the grid of one
/// worksheet (values, formulas, validation and named-range indicators, merges, sizes and styles; nothing is run).
/// </summary>
public static class WorkbookIntake
{
    public const int InventoryLimit = 2000;

    public static WorkbookSummary Analyze(WorkbookData wb)
    {
        var inventory = new List<FormulaInfo>();
        var classes = new Dictionary<string, int>();
        var total = 0;
        foreach (var s in wb.Sheets)
            foreach (var cell in s.Cells.Values.Where(c => c.Formula is not null).OrderBy(c => c.R).ThenBy(c => c.C))
            {
                total++;
                var cls = FormulaTranslator.Classify(cell.Formula!, wb.HasMacros, out var warn);
                foreach (var c in cls) classes[c] = classes.GetValueOrDefault(c) + 1;
                if (inventory.Count < InventoryLimit)
                {
                    var (fns, refs, _, _, _) = FormulaTranslator.Scan(cell.Formula!);
                    inventory.Add(new FormulaInfo(FormulaRefs.Address(s.Name, cell.R, cell.C), "=" + cell.Formula!.TrimStart('='), cell.Cached, cls, fns, refs, warn));
                }
            }
        var security = new List<string>();
        if (wb.HasMacros) security.Add("The workbook contains macro code. It is kept in the original file and is never run.");
        foreach (var d in wb.DdeLinks) security.Add($"Dynamic data exchange (DDE) link {d}: never run.");
        foreach (var e in wb.ExternalLinks) security.Add($"Link to another workbook ({e}): never followed.");
        foreach (var s in wb.Sheets)
            foreach (var c in s.Cells.Values.Where(c => c.Hyperlink is { } h && Suspicious(h)))
                security.Add($"Suspicious hyperlink at {FormulaRefs.Address(s.Name, c.R, c.C)}: {c.Hyperlink}");
        foreach (var s in wb.Sheets)
            foreach (var c in s.Cells.Values.Where(c => c.Formula is null && c.Kind == "text" && CsvParser.SafeCell(c.Text) != c.Text && (c.Text ?? "").Length > 1 && "=+-@".Contains(c.Text![0]) && !double.TryParse(c.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)).Take(20))
                security.Add($"Text that looks like a formula at {FormulaRefs.Address(s.Name, c.R, c.C)} (possible formula injection); kept as text.");
        var sheets = wb.Sheets.Select(s => new SheetInfo(s.Name, s.Index, s.Visibility, Math.Max(0, s.LastRow - s.FirstRow + 1), Math.Max(0, s.LastCol - s.FirstCol + 1), s.Cells.Count,
            s.Cells.Values.Count(c => c.Formula is not null), s.Validations.Count, s.Conditionals.Count, s.Tables.Count, s.Merges.Count, s.Images, s.Charts, s.Pivots, s.Comments, s.Hyperlinks,
            s.Protected, s.PrintArea, s.Orientation, s.FreezeRows, s.FreezeCols, s.AutoFilter)).ToList();
        return new WorkbookSummary(wb.Format, wb.CalcMode, wb.HasMacros, sheets, wb.Names, wb.ExternalLinks, wb.DdeLinks, wb.Warnings, wb.Fonts, classes, total, inventory, security);
    }

    static bool Suspicious(string h)
    {
        var x = h.Trim().ToLowerInvariant();
        return x.StartsWith("file:") || x.StartsWith("javascript:") || x.StartsWith("vbscript:") || x.StartsWith("data:") || x.StartsWith("\\\\") || x.Contains(".exe") || x.Contains(".bat") || x.Contains(".cmd") || x.Contains(".ps1");
    }

    /// <summary>The visible grid of a worksheet, capped for the browser. Formulas are shown with the result stored in the file.</summary>
    public static GridPreview Grid(WorkbookData wb, string? sheetName, int maxRows = 300, int maxCols = 60)
    {
        var sh = (sheetName is null ? wb.Sheets.FirstOrDefault(s => s.Visibility == "visible") : wb.Sheet(sheetName)) ?? wb.Sheets.FirstOrDefault()
            ?? throw new AppException("The workbook has no worksheets.");
        var r1 = Math.Min(sh.FirstRow, 1); var c1 = Math.Min(sh.FirstCol, 1);
        var r2 = Math.Max(r1, sh.LastRow); var c2 = Math.Max(c1, sh.LastCol);
        var truncated = r2 - r1 + 1 > maxRows || c2 - c1 + 1 > maxCols;
        r2 = Math.Min(r2, r1 + maxRows - 1); c2 = Math.Min(c2, c1 + maxCols - 1);
        var names = new Dictionary<(int, int), string>();
        foreach (var n in wb.Names.Where(n => n.Visible))
            if (FormulaRefs.Range(n.RefersTo) is { } rg && string.Equals((rg.Sheet ?? n.Scope)?.Trim('\''), sh.Name, StringComparison.OrdinalIgnoreCase))
                for (var r = rg.R1; r <= Math.Min(rg.R2, r2); r++) for (var c = rg.C1; c <= Math.Min(rg.C2, c2); c++) names.TryAdd((r, c), n.Name);
        var cells = new List<GridCell>();
        for (var r = r1; r <= r2; r++)
            for (var c = c1; c <= c2; c++)
            {
                if (sh.MergeAt(r, c) is { } m && (m.R1 != r || m.C1 != c)) continue;
                var x = sh.At(r, c);
                var val = sh.Validations.Any(v => r >= v.R1 && r <= v.R2 && c >= v.C1 && c <= v.C2);
                names.TryGetValue((r, c), out var nm);
                var (rs, cs) = sh.MergeAt(r, c) is { } mm ? (Math.Min(mm.R2, r2) - r + 1, Math.Min(mm.C2, c2) - c + 1) : (1, 1);
                if (x is null && !val && nm is null && rs == 1 && cs == 1) continue;
                cells.Add(new GridCell(r, c, x?.Formula is not null ? x.Cached : x?.Text, x?.Formula is null ? null : "=" + x.Formula.TrimStart('='), x?.Cached, val, nm, rs, cs, x?.Style, x?.Locked ?? true, x?.Comment));
            }
        var widths = Enumerable.Range(c1, c2 - c1 + 1).Select(sh.Width).ToList();
        var heights = Enumerable.Range(r1, r2 - r1 + 1).Select(sh.Height).ToList();
        return new GridPreview(sh.Name, r1, c1, r2, c2, widths, heights, cells, truncated, wb.Sheets.Select(s => s.Name).ToList());
    }

    /// <summary>Picks the header row of a data worksheet: the first row in the top 20 whose cells are mostly non-empty text.</summary>
    public static int DetectHeaderRow(XlSheet sh)
    {
        var width = Math.Max(1, sh.LastCol - sh.FirstCol + 1);
        for (var r = sh.FirstRow; r <= Math.Min(sh.LastRow, sh.FirstRow + 19); r++)
        {
            var row = Enumerable.Range(sh.FirstCol, width).Select(c => sh.At(r, c)).ToList();
            var text = row.Count(x => x is { Kind: "text", Formula: null } && !string.IsNullOrWhiteSpace(x.Text));
            var filled = row.Count(x => x is not null && x.Kind != "blank");
            if (filled >= Math.Max(2, width / 2) && text >= filled * 0.8) return r;
        }
        return sh.FirstRow;
    }

    /// <summary>The rows of a data worksheet below its header row, as text (dates ISO, numbers invariant; formula cells give the stored result).</summary>
    public static SheetTable Table(WorkbookData wb, string? sheetName, int? headerRow)
    {
        var sh = (sheetName is null ? wb.Sheets.FirstOrDefault(s => s.Visibility == "visible" && s.Cells.Count > 0) : wb.Sheet(sheetName))
            ?? throw new AppException(sheetName is null ? "The workbook has no worksheet with data." : $"The workbook has no worksheet named '{sheetName}'.");
        var hr = headerRow ?? DetectHeaderRow(sh);
        if (hr < 1 || hr > Math.Max(1, sh.LastRow)) throw new AppException("The header row is outside the worksheet.");
        var lastCol = sh.LastCol;
        while (lastCol > sh.FirstCol && Blank(sh.At(hr, lastCol))) lastCol--;
        var rows = new List<(int, string[])>();
        for (var r = hr + 1; r <= sh.LastRow; r++)
        {
            var vals = Enumerable.Range(1, lastCol).Select(c => Value(sh.At(r, c))).ToArray();
            if (vals.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add((r, vals));
        }
        var cols = new List<CsvColumn>();
        for (var c = 1; c <= lastCol; c++)
        {
            var name = sh.At(hr, c)?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { if (c < sh.FirstCol) continue; name = $"Column {FormulaRefs.ColumnLetters(c)}"; }
            var values = rows.Select(x => x.Item2[c - 1]).ToList();
            var type = Type(sh, hr, c, values);
            cols.Add(new CsvColumn(c - 1, FormulaRefs.ColumnLetters(c), name, type, values.Count(string.IsNullOrWhiteSpace), values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(5).ToList()!));
        }
        return new SheetTable(sh.Name, hr, cols, rows);
    }

    static string Type(XlSheet sh, int hr, int c, List<string> values)
    {
        var kinds = Enumerable.Range(hr + 1, Math.Max(0, Math.Min(sh.LastRow, hr + 200) - hr)).Select(r => sh.At(r, c)).Where(x => x is not null && x.Kind != "blank").Select(x => x!.Kind).Distinct().ToList();
        if (kinds.Count == 1) return kinds[0] switch { "number" => "number", "date" => "date", "bool" => "boolean", _ => CsvParser.Infer(values, '.', null) };
        return CsvParser.Infer(values, '.', null);
    }

    static bool Blank(XlCell? c) => c is null || c.Kind == "blank" || (c.Kind == "text" && string.IsNullOrWhiteSpace(c.Text));

    public static string Value(XlCell? c)
    {
        if (c is null) return "";
        var v = c.Formula is not null ? c.Cached : c.Text;
        return v ?? "";
    }
}
