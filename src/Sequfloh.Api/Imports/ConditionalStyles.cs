using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Sequfloh.Imports;

/// <summary>The fill, text colour and weight a conditional formatting rule gives one cell right now.</summary>
public sealed record ConditionalLook(int R, int C, string? Fill, string? Colour, bool Bold);

/// <summary>
/// Works out which conditional formatting rules apply to each cell from the cells' current values, the same way Excel
/// does for the common rule kinds: cell value comparisons, formula rules, text contains / begins / ends, blanks and errors.
/// </summary>
public static partial class ConditionalStyles
{
    public static List<ConditionalLook> Evaluate(XLWorkbook wb, XlSheet sh, int r1, int c1, int r2, int c2)
    {
        var looks = new Dictionary<(int, int), ConditionalLook>();
        if (!wb.TryGetWorksheet(sh.Name, out var ws)) return [];
        var done = new HashSet<(int, int)>();
        foreach (var rule in sh.Conditionals.OrderBy(x => x.Priority))
            for (var r = Math.Max(rule.R1, r1); r <= Math.Min(rule.R2, r2); r++)
                for (var c = Math.Max(rule.C1, c1); c <= Math.Min(rule.C2, c2); c++)
                {
                    if (done.Contains((r, c))) continue;
                    bool hit;
                    try { hit = Matches(ws, rule, r, c); }
                    catch (Exception e) when (e is not OutOfMemoryException) { hit = false; }
                    if (!hit) continue;
                    looks.TryGetValue((r, c), out var prev);
                    // the first matching rule wins for each property it sets, like Excel
                    looks[(r, c)] = new ConditionalLook(r, c, prev?.Fill ?? rule.Style.Fill, prev?.Colour ?? rule.Style.Colour, (prev?.Bold ?? false) || rule.Style.Bold);
                    if (rule.StopIfTrue) done.Add((r, c));
                }
        return [.. looks.Values];
    }

    static bool Matches(IXLWorksheet ws, XlConditional rule, int r, int c)
    {
        var cell = ws.Cell(r, c);
        var v = cell.Value;
        int dr = r - (rule.OriginR > 0 ? rule.OriginR : rule.R1), dc = c - (rule.OriginC > 0 ? rule.OriginC : rule.C1);
        string here = cell.Address.ToStringRelative();
        XLCellValue Operand(int i) => i < rule.Values.Count ? Value(ws, Shift(rule.Values[i], dr, dc), here) : Blank.Value;
        string text = v.IsBlank ? "" : v.IsError ? "" : cell.GetFormattedString();
        switch (rule.Type)
        {
            case "cellIs":
                {
                    var cmp = Compare(v, Operand(0));
                    return rule.Operator switch
                    {
                        "Equal" => cmp == 0, "NotEqual" => cmp != 0, "GreaterThan" => cmp > 0, "LessThan" => cmp < 0,
                        "EqualOrGreaterThan" => cmp >= 0, "EqualOrLessThan" => cmp <= 0,
                        "Between" => Between(v, Operand(0), Operand(1)), "NotBetween" => !Between(v, Operand(0), Operand(1)),
                        _ => false,
                    };
                }
            case "expression":
                return rule.Values.Count > 0 && Truthy(Value(ws, Shift(rule.Values[^1], dr, dc), here));
            case "containsText": return rule.Values.Count > 0 && text.Contains(Literal(rule.Values[0]), StringComparison.OrdinalIgnoreCase);
            case "notContainsText": return rule.Values.Count > 0 && !text.Contains(Literal(rule.Values[0]), StringComparison.OrdinalIgnoreCase);
            case "startsWith": return rule.Values.Count > 0 && text.StartsWith(Literal(rule.Values[0]), StringComparison.OrdinalIgnoreCase);
            case "endsWith": return rule.Values.Count > 0 && text.EndsWith(Literal(rule.Values[0]), StringComparison.OrdinalIgnoreCase);
            case "isBlank": return v.IsBlank || (v.IsText && v.GetText().Trim().Length == 0);
            case "notBlank": return !(v.IsBlank || (v.IsText && v.GetText().Trim().Length == 0));
            case "isError": return v.IsError;
            case "notError": return !v.IsError;
            default: return false;
        }
    }

    static string Literal(string s) => s.Trim().Trim('"');

    static XLCellValue Value(IXLWorksheet ws, string expr, string here)
    {
        var e = expr.Trim().TrimStart('=');
        if (e.Length == 0) return Blank.Value;
        if (double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return ws.Evaluate(e, here);
    }

    static bool Truthy(XLCellValue v) => v.IsBoolean ? v.GetBoolean() : v.IsNumber ? v.GetNumber() != 0 : false;

    static double? Num(XLCellValue v) => v.IsNumber ? v.GetNumber() : v.IsDateTime ? v.GetDateTime().ToOADate() : v.IsTimeSpan ? v.GetTimeSpan().TotalDays
        : v.IsText && double.TryParse(v.GetText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static int Compare(XLCellValue a, XLCellValue b)
    {
        if (a.IsBlank && b.IsBlank) return 0;
        var (x, y) = (Num(a), Num(b));
        if (x is not null && y is not null) return x.Value.CompareTo(y.Value);
        if (a.IsBlank && y is not null) return 0d.CompareTo(y.Value);
        return string.Compare(a.IsBlank ? "" : a.ToString(CultureInfo.InvariantCulture), b.IsBlank ? "" : b.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    static bool Between(XLCellValue v, XLCellValue lo, XLCellValue hi)
    {
        if (Compare(lo, hi) > 0) (lo, hi) = (hi, lo);
        return Compare(v, lo) >= 0 && Compare(v, hi) <= 0;
    }

    [GeneratedRegex(@"(""[^""]*"")|(?<![A-Za-z0-9_\.])(\$?)([A-Za-z]{1,3})(\$?)(\d{1,7})(?![A-Za-z0-9_\(])")]
    private static partial Regex RefPattern();

    /// <summary>Moves the relative parts of the A1 references in a rule formula from the rule's first cell to another cell.</summary>
    public static string Shift(string formula, int dr, int dc)
    {
        if (dr == 0 && dc == 0) return formula;
        return RefPattern().Replace(formula, m =>
        {
            if (m.Groups[1].Success) return m.Value;
            var col = FromLetters(m.Groups[3].Value);
            var row = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            if (m.Groups[2].Value.Length == 0) col += dc;
            if (m.Groups[4].Value.Length == 0) row += dr;
            if (col < 1 || row < 1) return "#REF!";
            return $"{m.Groups[2].Value}{ToLetters(col)}{m.Groups[4].Value}{row}";
        });
    }

    static int FromLetters(string s) { var n = 0; foreach (var ch in s.ToUpperInvariant()) n = n * 26 + (ch - 'A' + 1); return n; }
    static string ToLetters(int n) { var s = ""; while (n > 0) { var m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; } return s; }
}
