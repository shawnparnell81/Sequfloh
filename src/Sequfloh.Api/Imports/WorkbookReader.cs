using Sequfloh.Application.Abstractions;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace Sequfloh.Imports;

/// <summary>One cell as read from the workbook. Formulas are never calculated here: <see cref="Cached"/> is the value the file stored.</summary>
public sealed record XlCell(int R, int C, string Kind, string? Text, string? Formula, string? Cached, CellStyle? Style, bool Locked, string? NumberFormat, string? Comment, string? Hyperlink);

public sealed record XlValidation(string Range, int R1, int C1, int R2, int C2, string Type, string? Operator, string? Value, string? Value2, List<string>? Options, string? ListSource, bool Dropdown);
public sealed record XlConditional(string Range, int R1, int C1, int R2, int C2, string Type, string? Operator, List<string> Values, CellStyle Style, bool StopIfTrue, int Priority, int OriginR = 0, int OriginC = 0);
public sealed record XlTable(string Name, int R1, int C1, int R2, int C2, bool HeaderRow, bool TotalsRow, List<string> Columns, List<string?> TotalsFunctions);
public sealed record XlName(string Name, string RefersTo, string? Scope, bool Visible);
public sealed record XlCheckbox(string Sheet, int? R, int? C, bool Checked);

public sealed class XlSheet
{
    public string Name { get; init; } = "";
    public int Index { get; init; }
    /// <summary>visible, hidden or veryHidden.</summary>
    public string Visibility { get; init; } = "visible";
    public int FirstRow { get; set; } = 1;
    public int FirstCol { get; set; } = 1;
    public int LastRow { get; set; }
    public int LastCol { get; set; }
    /// <summary>The worksheet has more rows than are read for the in-app view; the file itself keeps every row.</summary>
    public bool Truncated { get; set; }
    public Dictionary<(int R, int C), XlCell> Cells { get; } = [];
    public List<(int R1, int C1, int R2, int C2)> Merges { get; } = [];
    /// <summary>Column widths and row heights in points (hidden ones are 0).</summary>
    public Dictionary<int, double> ColumnWidths { get; } = [];
    public Dictionary<int, double> RowHeights { get; } = [];
    public double DefaultRowHeight { get; set; } = 15;
    public double DefaultColumnWidth { get; set; } = 48;
    public bool Protected { get; set; }
    public int FreezeRows { get; set; }
    public int FreezeCols { get; set; }
    public string Orientation { get; set; } = "portrait";
    public int PaperSize { get; set; } = 1;
    public (double Left, double Right, double Top, double Bottom) MarginsMm { get; set; } = (18, 18, 19, 19);
    public string? PrintArea { get; set; }
    public (int From, int To)? PrintTitleRows { get; set; }
    public BandText? Header { get; set; }
    public BandText? Footer { get; set; }
    public List<int> RowBreaks { get; } = [];
    public string? AutoFilter { get; set; }
    public List<XlValidation> Validations { get; } = [];
    public List<XlConditional> Conditionals { get; } = [];
    public List<XlTable> Tables { get; } = [];
    public List<XlCheckbox> Checkboxes { get; } = [];
    public int Images { get; set; }
    public int Charts { get; set; }
    public int Pivots { get; set; }
    public int Comments { get; set; }
    public int Hyperlinks { get; set; }
    public double Width(int c) => ColumnWidths.TryGetValue(c, out var w) ? w : DefaultColumnWidth;
    public double Height(int r) => RowHeights.TryGetValue(r, out var h) ? h : DefaultRowHeight;
    public XlCell? At(int r, int c) => Cells.GetValueOrDefault((r, c));
    /// <summary>The merged range whose top-left cell is (r, c), or the range that covers (r, c).</summary>
    public (int R1, int C1, int R2, int C2)? MergeAt(int r, int c) => Merges.Cast<(int, int, int, int)?>().FirstOrDefault(m => r >= m!.Value.Item1 && r <= m.Value.Item3 && c >= m.Value.Item2 && c <= m.Value.Item4);
}

public sealed class WorkbookData
{
    public string Format { get; init; } = "xlsx";
    public string CalcMode { get; set; } = "auto";
    public bool HasMacros { get; set; }
    public List<XlSheet> Sheets { get; } = [];
    public List<XlName> Names { get; } = [];
    public List<string> ExternalLinks { get; } = [];
    public List<string> DdeLinks { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Fonts { get; } = [];
    public XlSheet? Sheet(string name) => Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads an Office Open XML workbook (XLSX, or XLSM whose macros are never run) into a neutral model: worksheets in order with hidden state, cell values and types,
/// formulas with cached results, styles, merges, column widths, row heights, named ranges, tables, data validation, conditional formatting, form-control
/// checkboxes, print setup, headers and footers, and counts of objects that are preserved only in the original file (charts, pivot tables, images).
/// Nothing in the file is executed and external links are never followed.
/// </summary>
public static class WorkbookReader
{
    public const int MaxRows = 5000, MaxCols = 200, MaxCells = 400_000;
    public const string ToolVersion = "workbook-reader/1";

    public static WorkbookData Read(Stream input, string format)
    {
        // a seekable copy: ClosedXML and the raw package pass both read it
        var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        var data = new WorkbookData { Format = format };
        var raw = ReadPackage(ms, data);
        ms.Position = 0;
        XLWorkbook wb;
        try { wb = new XLWorkbook(ms, new ClosedXML.Excel.LoadOptions { RecalculateAllFormulas = false }); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw new AppException($"The workbook could not be read: {e.Message}"); }
        using (wb)
        {
            // files written by scripts or some exporters carry no stored formula results; work them out so the sheet is not blank
            if (wb.Worksheets.Any(w => w.CellsUsed(c => c.HasFormula && c.CachedValue.IsBlank || c.HasFormula && c.CachedValue.IsText && c.CachedValue.GetText().Length == 0).Any()))
                try { wb.RecalculateAllFormulas(); } catch (Exception e) when (e is not OutOfMemoryException) { data.Warnings.Add("Some formula results could not be worked out until the file is opened in Excel."); }
            data.CalcMode = wb.CalculateMode switch { XLCalculateMode.Manual => "manual", XLCalculateMode.AutoNoTable => "auto-except-tables", _ => "auto" };
            foreach (var dn in wb.DefinedNames)
                data.Names.Add(new XlName(dn.Name, dn.RefersTo ?? "", null, dn.Visible));
            var cellCount = 0;
            foreach (var ws in wb.Worksheets.OrderBy(w => w.Position))
            {
                var sh = new XlSheet
                {
                    Name = ws.Name, Index = ws.Position - 1,
                    Visibility = ws.Visibility switch { XLWorksheetVisibility.Hidden => "hidden", XLWorksheetVisibility.VeryHidden => "veryHidden", _ => "visible" },
                };
                foreach (var dn in ws.DefinedNames) data.Names.Add(new XlName(dn.Name, dn.RefersTo ?? "", ws.Name, dn.Visible));
                ReadSheet(wb, ws, sh, data, ref cellCount);
                if (raw.TryGetValue(ws.Name, out var extra))
                {
                    sh.Charts = extra.Charts; sh.Pivots = extra.Pivots;
                    sh.Checkboxes.AddRange(extra.Checkboxes);
                }
                data.Sheets.Add(sh);
            }
        }
        return data;
    }

    static void ReadSheet(XLWorkbook wb, IXLWorksheet ws, XlSheet sh, WorkbookData data, ref int cellCount)
    {
        sh.Protected = ws.IsProtected;
        sh.DefaultRowHeight = ws.RowHeight;
        sh.DefaultColumnWidth = WidthPoints(ws.ColumnWidth);
        if (ws.SheetView.SplitRow > 0) sh.FreezeRows = ws.SheetView.SplitRow;
        if (ws.SheetView.SplitColumn > 0) sh.FreezeCols = ws.SheetView.SplitColumn;
        var ps = ws.PageSetup;
        sh.Orientation = ps.PageOrientation == XLPageOrientation.Landscape ? "landscape" : "portrait";
        sh.PaperSize = (int)ps.PaperSize;
        sh.MarginsMm = (ps.Margins.Left * 25.4, ps.Margins.Right * 25.4, ps.Margins.Top * 25.4, ps.Margins.Bottom * 25.4);
        if (ps.PrintAreas.Any()) sh.PrintArea = string.Join(",", ps.PrintAreas.Select(a => a.RangeAddress.ToStringRelative(false)));
        if (ps.FirstRowToRepeatAtTop > 0) sh.PrintTitleRows = (ps.FirstRowToRepeatAtTop, ps.LastRowToRepeatAtTop);
        sh.Header = Band(ps.Header); sh.Footer = Band(ps.Footer);
        sh.RowBreaks.AddRange(ps.RowBreaks);
        if (ws.AutoFilter.IsEnabled) sh.AutoFilter = ws.AutoFilter.Range?.RangeAddress.ToStringRelative(false);
        sh.Images = ws.Pictures.Count;

        var used = ws.RangeUsed(XLCellsUsedOptions.All);
        if (used is null) { sh.LastRow = 0; sh.LastCol = 0; return; }
        sh.FirstRow = used.RangeAddress.FirstAddress.RowNumber; sh.FirstCol = used.RangeAddress.FirstAddress.ColumnNumber;
        sh.LastRow = used.RangeAddress.LastAddress.RowNumber; sh.LastCol = used.RangeAddress.LastAddress.ColumnNumber;
        // merged areas extend the grid
        foreach (var m in ws.MergedRanges)
        {
            var a = m.RangeAddress;
            sh.Merges.Add((a.FirstAddress.RowNumber, a.FirstAddress.ColumnNumber, a.LastAddress.RowNumber, a.LastAddress.ColumnNumber));
            sh.LastRow = Math.Max(sh.LastRow, a.LastAddress.RowNumber); sh.LastCol = Math.Max(sh.LastCol, a.LastAddress.ColumnNumber);
        }
        if (sh.LastRow - sh.FirstRow + 1 > MaxRows) { data.Warnings.Add($"{ws.Name}: only the first {MaxRows} rows are shown in the app; the file keeps every row."); sh.LastRow = sh.FirstRow + MaxRows - 1; sh.Truncated = true; }
        if (sh.LastCol - sh.FirstCol + 1 > MaxCols) { data.Warnings.Add($"{ws.Name}: only the first {MaxCols} columns are read."); sh.LastCol = sh.FirstCol + MaxCols - 1; }
        for (var c = sh.FirstCol; c <= sh.LastCol; c++)
        {
            var col = ws.Column(c);
            sh.ColumnWidths[c] = col.IsHidden ? 0 : WidthPoints(col.Width);
        }
        for (var r = sh.FirstRow; r <= sh.LastRow; r++)
        {
            var row = ws.Row(r);
            sh.RowHeights[r] = row.IsHidden ? 0 : row.Height;
        }

        foreach (var cell in ws.Range(sh.FirstRow, sh.FirstCol, sh.LastRow, sh.LastCol).CellsUsed(XLCellsUsedOptions.All))
        {
            if (++cellCount > MaxCells) { data.Warnings.Add($"Only the first {MaxCells:N0} cells of the workbook are read."); break; }
            var r = cell.Address.RowNumber; var c = cell.Address.ColumnNumber;
            string kind; string? text = null, formula = null, cached = null;
            if (cell.HasFormula)
            {
                formula = cell.FormulaA1;
                if (string.IsNullOrEmpty(formula) && cell.HasArrayFormula) formula = cell.FormulaA1;
                kind = "formula";
                cached = Show(cell.CachedValue);
            }
            else
            {
                var v = cell.Value;
                (kind, text) = v.Type switch
                {
                    XLDataType.Blank => ("blank", (string?)null),
                    XLDataType.Boolean => ("bool", v.GetBoolean() ? "TRUE" : "FALSE"),
                    XLDataType.Number => ("number", v.GetNumber().ToString("0.###############", CultureInfo.InvariantCulture)),
                    XLDataType.DateTime => ("date", v.GetDateTime().ToString(v.GetDateTime().TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)),
                    XLDataType.TimeSpan => ("text", v.GetTimeSpan().ToString()),
                    XLDataType.Error => ("error", v.GetError().ToString()),
                    _ => ("text", v.GetText()),
                };
                if (kind == "text" && cell.HasRichText) text = cell.GetRichText().Text;
            }
            string? comment = cell.HasComment ? cell.GetComment().Text : null;
            if (comment is not null) sh.Comments++;
            string? link = null;
            if (cell.HasHyperlink) { sh.Hyperlinks++; link = cell.GetHyperlink().IsExternal ? cell.GetHyperlink().ExternalAddress?.ToString() : cell.GetHyperlink().InternalAddress; }
            var style = Style(wb, cell.Style, data);
            var nf = cell.Style.NumberFormat.Format;
            if (string.IsNullOrEmpty(nf)) nf = BuiltInFormat(cell.Style.NumberFormat.NumberFormatId);
            sh.Cells[(r, c)] = new XlCell(r, c, kind, text, formula, cached, style, cell.Style.Protection.Locked, nf, comment, link);
        }

        foreach (var dv in ws.DataValidations)
            foreach (var range in dv.Ranges)
            {
                var a = range.RangeAddress;
                var type = dv.AllowedValues switch
                {
                    XLAllowedValues.List => "list", XLAllowedValues.WholeNumber => "whole", XLAllowedValues.Decimal => "decimal", XLAllowedValues.Date => "date",
                    XLAllowedValues.Time => "time", XLAllowedValues.TextLength => "textLength", XLAllowedValues.Custom => "custom", _ => "any",
                };
                if (type == "any") continue;
                List<string>? options = null; string? source = null;
                if (type == "list")
                {
                    var v = dv.Value?.Trim() ?? "";
                    if (v.StartsWith('"')) options = v.Trim('"').Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                    else if (v.Length > 0 && !v.StartsWith('=') && !Regex.IsMatch(v, @"^[\$A-Za-z0-9_'!:\. ]+$")) options = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                    else if (v.Length > 0 && v.Contains(',') && !v.Contains('!') && !v.Contains(':')) options = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
                    else source = v.TrimStart('=');
                }
                var op = dv.Operator.ToString();
                sh.Validations.Add(new XlValidation(a.ToStringRelative(false), a.FirstAddress.RowNumber, a.FirstAddress.ColumnNumber, a.LastAddress.RowNumber, a.LastAddress.ColumnNumber,
                    type, op, dv.MinValue, dv.MaxValue, options, source, dv.InCellDropdown));
            }

        foreach (var cf in ws.ConditionalFormats)
        {
            var a = cf.Range.RangeAddress;
            var vals = cf.Values.OrderBy(v => v.Key).Select(v => v.Value?.Value ?? "").ToList();
            var type = cf.ConditionalFormatType switch
            {
                XLConditionalFormatType.CellIs => "cellIs", XLConditionalFormatType.Expression => "expression", XLConditionalFormatType.ContainsText => "containsText",
                XLConditionalFormatType.NotContainsText => "notContainsText", XLConditionalFormatType.StartsWith => "startsWith", XLConditionalFormatType.EndsWith => "endsWith",
                XLConditionalFormatType.IsBlank => "isBlank", XLConditionalFormatType.NotBlank => "notBlank", XLConditionalFormatType.IsError => "isError", XLConditionalFormatType.NotError => "notError",
                XLConditionalFormatType.ColorScale => "colorScale", XLConditionalFormatType.DataBar => "dataBar", XLConditionalFormatType.IconSet => "iconSet",
                XLConditionalFormatType.Top10 => "top10", XLConditionalFormatType.IsDuplicate => "duplicate", XLConditionalFormatType.IsUnique => "unique", XLConditionalFormatType.AboveAverage => "aboveAverage",
                XLConditionalFormatType.TimePeriod => "timePeriod", var x => x.ToString(),
            };
            // one rule can cover several ranges ("F2:G5 F6 F7:G51"); relative references in its formula start from the first range's top-left cell
            var style = Style(wb, cf.Style, data) ?? new CellStyle();
            var priority = sh.Conditionals.Count + 1;
            var parts = cf.Ranges.Count > 0 ? cf.Ranges.Select(x => x.RangeAddress).ToList() : [a];
            foreach (var p in parts)
                sh.Conditionals.Add(new XlConditional(p.ToStringRelative(false), p.FirstAddress.RowNumber, p.FirstAddress.ColumnNumber, p.LastAddress.RowNumber, p.LastAddress.ColumnNumber,
                    type, cf.Operator.ToString(), vals, style, cf.StopIfTrue, priority, a.FirstAddress.RowNumber, a.FirstAddress.ColumnNumber));
        }

        foreach (var t in ws.Tables)
        {
            var a = t.RangeAddress;
            sh.Tables.Add(new XlTable(t.Name, a.FirstAddress.RowNumber, a.FirstAddress.ColumnNumber, a.LastAddress.RowNumber, a.LastAddress.ColumnNumber, t.ShowHeaderRow, t.ShowTotalsRow,
                t.Fields.Select(f => f.Name).ToList(), t.Fields.Select(f => t.ShowTotalsRow ? (f.TotalsRowFunction == XLTotalsRowFunction.None ? null : f.TotalsRowFunction.ToString()) : null).ToList()));
        }
    }

    /// <summary>Excel column width (characters of the default font) to points.</summary>
    public static double WidthPoints(double chars) => chars <= 0 ? 0 : Math.Round(Math.Truncate((256 * chars + Math.Truncate(128.0 / 7)) / 256 * 7) * 0.75, 2);

    static string? Show(XLCellValue v) => v.Type switch
    {
        XLDataType.Blank => null,
        XLDataType.Boolean => v.GetBoolean() ? "TRUE" : "FALSE",
        XLDataType.Number => v.GetNumber().ToString("0.###############", CultureInfo.InvariantCulture),
        XLDataType.DateTime => v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XLDataType.Error => v.GetError() switch { XLError.DivisionByZero => "#DIV/0!", XLError.NoValueAvailable => "#N/A", XLError.IncompatibleValue => "#VALUE!", XLError.CellReference => "#REF!", XLError.NameNotRecognized => "#NAME?", XLError.NumberInvalid => "#NUM!", _ => "#NULL!" },
        _ => v.ToString(CultureInfo.InvariantCulture),
    };

    static BandText? Band(IXLHeaderFooter hf)
    {
        string? T(IXLHFItem i)
        {
            var s = new[] { XLHFOccurrence.AllPages, XLHFOccurrence.OddPages, XLHFOccurrence.FirstPage }.Select(o => { try { return i.GetText(o); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; } })
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            return s is null ? null : HeaderTokens(s);
        }
        var b = new BandText { Left = T(hf.Left), Center = T(hf.Center), Right = T(hf.Right) };
        return b.Left is null && b.Center is null && b.Right is null ? null : b;
    }

    /// <summary>Excel header/footer codes to print tokens: &amp;P page, &amp;N pages, &amp;D/&amp;T printed, &amp;F/&amp;A file and sheet names; font codes removed.</summary>
    public static string HeaderTokens(string s)
    {
        s = Regex.Replace(s, "&\"[^\"]*\"", "");
        s = Regex.Replace(s, @"&\d{1,3}", "");
        s = s.Replace("&P", "{page}").Replace("&N", "{pages}").Replace("&D", "{printed}").Replace("&T", "").Replace("&F", "{template}").Replace("&A", "{title}").Replace("&Z", "");
        s = Regex.Replace(s, "&[BIUSEXYKGLCR]", "");
        return s.Replace("&&", "&").Trim();
    }

    static readonly Dictionary<int, string> BuiltIn = new()
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [14] = "m/d/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yyyy h:mm", [44] = "_($* #,##0.00_)", [49] = "@",
    };
    static string? BuiltInFormat(int id) => BuiltIn.GetValueOrDefault(id);

    public static bool IsDateFormat(string? nf) => nf is not null && !nf.Contains("General") && Regex.IsMatch(Regex.Replace(nf, "\"[^\"]*\"|\\[[^\\]]*\\]", ""), "[dy]|m{3,}|(^|[^h:])mm?([^:]|$)", RegexOptions.IgnoreCase) && !nf.Contains('#') && !nf.Contains('0');
    public static bool IsPercentFormat(string? nf) => nf is not null && nf.Contains('%');
    public static bool IsCurrencyFormat(string? nf) => nf is not null && (nf.Contains('$') || nf.Contains('€') || nf.Contains('£') || nf.Contains("[$"));
    public static int? Decimals(string? nf)
    {
        if (nf is null) return null;
        var m = Regex.Match(nf, @"0\.(0+)");
        return m.Success ? m.Groups[1].Length : Regex.IsMatch(nf, "^[#,0]+%?$") ? 0 : null;
    }

    static CellStyle? Style(XLWorkbook wb, IXLStyle st, WorkbookData data)
    {
        var s = new CellStyle();
        var font = st.Font;
        s.FontName = font.FontName;
        if (!string.IsNullOrEmpty(font.FontName) && !data.Fonts.Contains(font.FontName)) data.Fonts.Add(font.FontName);
        s.Font = Face(font.FontName);
        s.Size = font.FontSize is > 0 and var fs ? Math.Clamp(fs, 4, 72) : null;
        s.Bold = font.Bold; s.Italic = font.Italic; s.Underline = font.Underline != XLFontUnderlineValues.None;
        s.Colour = Hex(wb, font.FontColor, "#000000");
        if (st.Fill.PatternType != XLFillPatternValues.None)
            s.Fill = Hex(wb, st.Fill.PatternType == XLFillPatternValues.Solid ? st.Fill.BackgroundColor : st.Fill.PatternColor, null) ?? Hex(wb, st.Fill.BackgroundColor, null);
        if (s.Fill is "#FFFFFF") s.Fill = null;
        s.Align = st.Alignment.Horizontal switch { XLAlignmentHorizontalValues.Center or XLAlignmentHorizontalValues.CenterContinuous => "center", XLAlignmentHorizontalValues.Right => "right", XLAlignmentHorizontalValues.Left => "left", _ => null };
        s.VAlign = st.Alignment.Vertical switch { XLAlignmentVerticalValues.Top => "top", XLAlignmentVerticalValues.Center => "middle", _ => null };
        s.Wrap = st.Alignment.WrapText;
        var b = st.Border;
        s.BorderTop = Border(b.TopBorder); s.BorderRight = Border(b.RightBorder); s.BorderBottom = Border(b.BottomBorder); s.BorderLeft = Border(b.LeftBorder);
        if ((s.BorderTop ?? s.BorderRight ?? s.BorderBottom ?? s.BorderLeft) is not null)
            s.BorderColour = Hex(wb, s.BorderBottom is not null ? b.BottomBorderColor : s.BorderTop is not null ? b.TopBorderColor : s.BorderLeft is not null ? b.LeftBorderColor : b.RightBorderColor, "#000000");
        if (s.Colour == "#000000") s.Colour = null;
        if (s.BorderColour == "#000000") s.BorderColour = null;
        var nf = st.NumberFormat.Format;
        s.NumberFormat = string.IsNullOrEmpty(nf) ? BuiltInFormat(st.NumberFormat.NumberFormatId) : nf;
        if (s.NumberFormat is "General") s.NumberFormat = null;
        return s;
    }

    /// <summary>Closest standard face for a source font.</summary>
    public static string Face(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "sans";
        var n = name.ToLowerInvariant();
        if (n.Contains("courier") || n.Contains("consol") || n.Contains("mono") || n.Contains("lucida console")) return "mono";
        if (n.Contains("times") || n.Contains("georgia") || n.Contains("cambria") || n.Contains("garamond") || n.Contains("book") || n.Contains("serif") && !n.Contains("sans")) return "serif";
        return "sans";
    }

    static string? Border(XLBorderStyleValues b) => b switch
    {
        XLBorderStyleValues.None => null,
        XLBorderStyleValues.Thin => "thin",
        XLBorderStyleValues.Hair => "hair",
        XLBorderStyleValues.Medium => "medium",
        XLBorderStyleValues.Thick => "thick",
        XLBorderStyleValues.Double => "double",
        XLBorderStyleValues.Dashed or XLBorderStyleValues.MediumDashed or XLBorderStyleValues.DashDot or XLBorderStyleValues.MediumDashDot or XLBorderStyleValues.DashDotDot or XLBorderStyleValues.MediumDashDotDot or XLBorderStyleValues.SlantDashDot => "dashed",
        XLBorderStyleValues.Dotted => "dotted",
        _ => "thin",
    };

    static string? Hex(XLWorkbook wb, XLColor? c, string? fallback)
    {
        if (c is null || !c.HasValue) return fallback;
        try
        {
            System.Drawing.Color col;
            if (c.ColorType == XLColorType.Theme)
            {
                var resolved = wb.Theme.ResolveThemeColor(c.ThemeColor);
                col = resolved.Color;
                var tint = c.ThemeTint;
                if (tint != 0) col = Tint(col, tint);
            }
            else col = c.Color;
            // Excel ignores the alpha byte of an ARGB colour; some tools write 00 there for opaque colours
            if (col.A == 0 && (c.ColorType != XLColorType.Color || col.IsNamedColor)) return fallback;
            return $"#{col.R:X2}{col.G:X2}{col.B:X2}";
        }
        catch (Exception) { return fallback; }
    }

    static System.Drawing.Color Tint(System.Drawing.Color c, double tint)
    {
        int T(int v) => (int)Math.Round(tint < 0 ? v * (1 + tint) : v + (255 - v) * tint);
        return System.Drawing.Color.FromArgb(255, Math.Clamp(T(c.R), 0, 255), Math.Clamp(T(c.G), 0, 255), Math.Clamp(T(c.B), 0, 255));
    }

    // ---------------------------------------------------------------- package parts ClosedXML does not expose
    sealed record SheetExtras(int Charts, int Pivots, List<XlCheckbox> Checkboxes);

    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    static Dictionary<string, SheetExtras> ReadPackage(Stream s, WorkbookData data)
    {
        var result = new Dictionary<string, SheetExtras>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
            XDocument? Load(string path)
            {
                var e = zip.GetEntry(path);
                if (e is null || e.Length > 50 * 1024 * 1024) return null;
                using var es = e.Open();
                // no DTDs or external entities
                using var xr = System.Xml.XmlReader.Create(es, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                return XDocument.Load(xr);
            }
            static string Resolve(string baseDir, string target) => target.StartsWith('/') ? target.TrimStart('/') : Path.GetFullPath(Path.Combine("/" + baseDir, target)).TrimStart('/').Replace('\\', '/');

            data.HasMacros = zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/externalLinks/externalLink", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml")))
            {
                var x = Load(e.FullName);
                var dde = x?.Root?.Element(Main + "ddeLink");
                if (dde is not null) data.DdeLinks.Add($"{dde.Attribute("ddeService")?.Value}|{dde.Attribute("ddeTopic")?.Value}");
                var rels = Load($"xl/externalLinks/_rels/{Path.GetFileName(e.FullName)}.rels");
                foreach (var r in rels?.Root?.Elements(Pkg + "Relationship") ?? []) if (r.Attribute("TargetMode")?.Value == "External") data.ExternalLinks.Add(r.Attribute("Target")?.Value ?? "");
                if (dde is null && (rels?.Root?.Elements().Any() != true)) data.ExternalLinks.Add(Path.GetFileName(e.FullName));
            }
            var wbx = Load("xl/workbook.xml"); var wbr = Load("xl/_rels/workbook.xml.rels");
            if (wbx?.Root is null || wbr?.Root is null) return result;
            var relMap = wbr.Root.Elements(Pkg + "Relationship").ToDictionary(r => r.Attribute("Id")!.Value, r => Resolve("xl", r.Attribute("Target")!.Value));
            foreach (var sheet in wbx.Root.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
            {
                var name = sheet.Attribute("name")?.Value ?? "";
                var rid = sheet.Attribute(Rel + "id")?.Value;
                if (rid is null || !relMap.TryGetValue(rid, out var part)) continue;
                var dir = Path.GetDirectoryName(part)!.Replace('\\', '/');
                var srels = Load($"{dir}/_rels/{Path.GetFileName(part)}.rels");
                int charts = 0, pivots = 0; var boxes = new List<XlCheckbox>();
                foreach (var r in srels?.Root?.Elements(Pkg + "Relationship") ?? [])
                {
                    var type = r.Attribute("Type")?.Value ?? ""; var target = r.Attribute("Target")?.Value ?? "";
                    if (r.Attribute("TargetMode")?.Value == "External") continue;
                    if (type.EndsWith("/pivotTable")) pivots++;
                    else if (type.EndsWith("/drawing"))
                    {
                        var dpart = Resolve(dir, target);
                        var drels = Load($"{Path.GetDirectoryName(dpart)!.Replace('\\', '/')}/_rels/{Path.GetFileName(dpart)}.rels");
                        charts += drels?.Root?.Elements(Pkg + "Relationship").Count(x => (x.Attribute("Type")?.Value ?? "").EndsWith("/chart")) ?? 0;
                    }
                    else if (type.EndsWith("/ctrlProp"))
                    {
                        var cp = Load(Resolve(dir, target))?.Root;
                        if (cp is null || !string.Equals(cp.Attribute("objectType")?.Value, "CheckBox", StringComparison.OrdinalIgnoreCase)) continue;
                        var link = cp.Attribute("fmlaLink")?.Value?.Replace("$", "");
                        int? rr = null, cc = null;
                        if (link is not null)
                        {
                            var bang = link.LastIndexOf('!'); var addr = bang >= 0 ? link[(bang + 1)..] : link;
                            var m = Regex.Match(addr, "^([A-Za-z]{1,3})(\\d+)$");
                            if (m.Success) { cc = FormulaRefs.ColumnNumber(m.Groups[1].Value); rr = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
                        }
                        boxes.Add(new XlCheckbox(name, rr, cc, string.Equals(cp.Attribute("checked")?.Value, "Checked", StringComparison.OrdinalIgnoreCase)));
                    }
                }
                result[name] = new SheetExtras(charts, pivots, boxes);
            }
        }
        catch (InvalidDataException) { data.Warnings.Add("The workbook package could not be read completely."); }
        catch (System.Xml.XmlException) { data.Warnings.Add("Part of the workbook package is not valid XML and was skipped."); }
        return result;
    }
}
