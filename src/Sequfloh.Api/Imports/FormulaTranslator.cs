using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sequfloh.Imports;

public static class FormulaRefs
{
    public static int ColumnNumber(string letters) { var n = 0; foreach (var ch in letters.ToUpperInvariant()) n = n * 26 + (ch - 'A' + 1); return n; }
    public static string ColumnLetters(int n) { var s = ""; while (n > 0) { var m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; } return s; }
    public static string Address(int r, int c) => $"{ColumnLetters(c)}{r}";
    public static string Address(string sheet, int r, int c) => $"{sheet}!{ColumnLetters(c)}{r}";

    static readonly Regex CellRx = new(@"^\$?([A-Za-z]{1,3})\$?(\d{1,7})$", RegexOptions.Compiled);
    public static (int R, int C)? Cell(string a)
    {
        var m = CellRx.Match(a.Trim());
        return m.Success ? (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), ColumnNumber(m.Groups[1].Value)) : null;
    }

    /// <summary>Parses "Sheet1!$A$1:$B$4", "'My sheet'!A1" or "A1:A9" into a sheet (null = current) and a box.</summary>
    public static (string? Sheet, int R1, int C1, int R2, int C2)? Range(string text)
    {
        text = text.Trim().TrimStart('=');
        string? sheet = null;
        var bang = text.LastIndexOf('!');
        if (bang > 0) { sheet = text[..bang].Trim(); if (sheet.StartsWith('\'') && sheet.EndsWith('\'')) sheet = sheet[1..^1].Replace("''", "'"); text = text[(bang + 1)..]; }
        var parts = text.Split(':');
        var a = Cell(parts[0]); if (a is null) return null;
        var b = parts.Length > 1 ? Cell(parts[1]) : a; if (b is null) return null;
        return (sheet, Math.Min(a.Value.R, b.Value.R), Math.Min(a.Value.C, b.Value.C), Math.Max(a.Value.R, b.Value.R), Math.Max(a.Value.C, b.Value.C));
    }
}

/// <summary>A reference found in a spreadsheet formula, after relative parts were shifted by the formula's offset.</summary>
public sealed record XlRef(string Sheet, int R1, int C1, int R2, int C2, bool AbsRow1, bool AbsRow2)
{
    public bool IsCell => R1 == R2 && C1 == C2;
    public int Count => (R2 - R1 + 1) * (C2 - C1 + 1);
}

/// <summary>Formula categories.</summary>
public static class FormulaClass
{
    public const string Server = "server", Preserved = "preserved", Unsupported = "unsupported", External = "external", Macro = "macro", Volatile = "volatile", Review = "review";
}

public sealed record FormulaInfo(string Location, string Formula, string? Cached, List<string> Classes, List<string> Functions, List<string> Dependencies, string? Warning);

/// <summary>
/// Reads spreadsheet formulas for the import summary: the functions and references they use and how the app treats them. Formulas stay
/// spreadsheet formulas; the workbook engine recalculates them after an edit.
/// </summary>
public static class FormulaTranslator
{
    public static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase) { "NOW", "TODAY", "RAND", "RANDBETWEEN", "OFFSET", "INDIRECT", "CELL", "INFO", "RANDARRAY" };
    public static readonly HashSet<string> MacroFunctions = new(StringComparer.OrdinalIgnoreCase) { "CALL", "REGISTER", "REGISTER.ID", "EXEC", "EVALUATE", "GET.CELL", "RUN" };
    /// <summary>Functions the workbook engine may not recalculate exactly; the result stored in the file is shown until the cell is recalculated.</summary>
    public static readonly HashSet<string> PreservedOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "VLOOKUP", "HLOOKUP", "XLOOKUP", "LOOKUP", "OFFSET", "INDIRECT", "SUMPRODUCT", "FILTER", "SORT", "UNIQUE", "SEQUENCE", "TRANSPOSE", "MMULT", "FREQUENCY", "TREND", "FORECAST",
        "PMT", "NPV", "IRR", "RATE", "FV", "PV", "NORM.DIST", "NORMDIST", "NORM.INV", "NORMSINV", "NORM.S.INV", "NORM.S.DIST", "T.INV", "T.INV.2T", "TINV", "CHIINV", "F.INV", "PERCENTILE", "PERCENTILE.INC", "QUARTILE",
        "RANK", "RANK.EQ", "NETWORKDAYS", "WORKDAY", "DATEDIF", "WEEKNUM", "EOMONTH", "HOUR", "MINUTE", "SECOND", "TIME", "HYPERLINK", "ADDRESS", "ROW", "COLUMN", "ROWS", "COLUMNS", "TEXTJOIN", "LET", "LAMBDA",
    };

    enum K { Num, Str, Bool, Err, Ref, Name, Func, LParen, RParen, Comma, Op, Struct, Array, Space }
    sealed record Tok(K Kind, string Text);

    static readonly Regex SheetPrefix = new(@"^(?:'(?:[^']|'')+'|\[\d+\][^!\s]*|[A-Za-z_\u00C0-\uFFFF][\w\.\u00C0-\uFFFF]*)!", RegexOptions.Compiled);
    static readonly Regex AreaRef = new(@"^\$?[A-Za-z]{1,3}\$?\d{1,7}(?::\$?[A-Za-z]{1,3}\$?\d{1,7})?(?![\w\(])", RegexOptions.Compiled);
    static readonly Regex ColRange = new(@"^\$?[A-Za-z]{1,3}:\$?[A-Za-z]{1,3}(?![\w\(])", RegexOptions.Compiled);
    static readonly Regex RowRange = new(@"^\$?\d{1,7}:\$?\d{1,7}(?![\w\(])", RegexOptions.Compiled);

    static List<Tok> Tokenize(string f)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < f.Length)
        {
            var c = f[i];
            if (c == ' ') { var st = i; while (i < f.Length && f[i] == ' ') i++; list.Add(new(K.Space, f[st..i])); continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder("\""); i++;
                while (i < f.Length) { if (f[i] == '"') { if (i + 1 < f.Length && f[i + 1] == '"') { sb.Append("\"\""); i += 2; continue; } i++; break; } sb.Append(f[i++]); }
                list.Add(new(K.Str, sb.Append('"').ToString())); continue;
            }
            if (c == '{') { var st = i; while (i < f.Length && f[i] != '}') i++; i++; list.Add(new(K.Array, f[st..Math.Min(i, f.Length)])); continue; }
            if (c == '#') { var m = Regex.Match(f[i..], @"^#(NULL!|DIV/0!|VALUE!|REF!|NAME\?|NUM!|N/A|GETTING_DATA|SPILL!|CALC!)"); if (m.Success) { list.Add(new(K.Err, m.Value)); i += m.Length; continue; } }
            if (c == '(') { list.Add(new(K.LParen, "(")); i++; continue; }
            if (c == ')') { list.Add(new(K.RParen, ")")); i++; continue; }
            if (c is ',' or ';') { list.Add(new(K.Comma, ",")); i++; continue; }
            if (i + 1 < f.Length && f.Substring(i, 2) is "<=" or ">=" or "<>") { list.Add(new(K.Op, f.Substring(i, 2))); i += 2; continue; }
            if ("+-*/^&=<>%@".Contains(c)) { list.Add(new(K.Op, c.ToString())); i++; continue; }
            if (c == '[') { var st = i; var depth = 0; while (i < f.Length) { if (f[i] == '[') depth++; if (f[i] == ']') { depth--; if (depth == 0) { i++; break; } } i++; } list.Add(new(K.Struct, f[st..i])); continue; }
            var rest = f[i..];
            var sp = SheetPrefix.Match(rest);
            var prefix = sp.Success ? sp.Value : "";
            var after = rest[prefix.Length..];
            var ar = AreaRef.Match(after); var cr = ColRange.Match(after); var rr = RowRange.Match(after);
            if (ar.Success || cr.Success || rr.Success)
            {
                var m = ar.Success ? ar : cr.Success ? cr : rr;
                // a cell-looking name followed by "(" is a function (e.g. LOG10)
                list.Add(new(K.Ref, prefix + m.Value)); i += prefix.Length + m.Length; continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < f.Length && char.IsDigit(f[i + 1])))
            {
                var m = Regex.Match(rest, @"^\d*\.?\d+(?:[eE][+-]?\d+)?");
                list.Add(new(K.Num, m.Value)); i += m.Length; continue;
            }
            if (char.IsLetter(c) || c is '_' or '\\')
            {
                var m = Regex.Match(rest, @"^[A-Za-z_\\][\w\.]*");
                var word = m.Value; i += m.Length;
                if (i < f.Length && f[i] == '[') { var st = i; var depth = 0; while (i < f.Length) { if (f[i] == '[') depth++; if (f[i] == ']') { depth--; if (depth == 0) { i++; break; } } i++; } list.Add(new(K.Struct, word + f[st..i])); continue; }
                if (i < f.Length && f[i] == '(') list.Add(new(K.Func, word));
                else if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) list.Add(new(K.Bool, word.ToUpperInvariant()));
                else list.Add(new(K.Name, word));
                continue;
            }
            list.Add(new(K.Op, c.ToString())); i++;
        }
        return list;
    }

    public static string NormalizeFunction(string name)
    {
        var n = name.ToUpperInvariant();
        foreach (var p in new[] { "_XLFN._XLWS.", "_XLFN.", "_XLWS.", "_XLUDF." }) if (n.StartsWith(p)) n = n[p.Length..];
        return n;
    }

    /// <summary>Functions and references used by a formula, without translating it (used for the workbook inventory).</summary>
    public static (List<string> Functions, List<string> Refs, bool External, bool Dde, bool Array) Scan(string formula)
    {
        var toks = Tokenize(formula.TrimStart('='));
        var fns = toks.Where(t => t.Kind == K.Func).Select(t => NormalizeFunction(t.Text)).Distinct().ToList();
        var refs = toks.Where(t => t.Kind is K.Ref or K.Name or K.Struct).Select(t => t.Text).Distinct().ToList();
        var ext = toks.Any(t => t.Kind == K.Ref && Regex.IsMatch(t.Text, @"^'?\[|\.xls", RegexOptions.IgnoreCase)) || Regex.IsMatch(formula, @"\[[^\]]*\.xl[sx]", RegexOptions.IgnoreCase);
        var dde = Regex.IsMatch(formula, @"\w+\|'?[^!]*'?!", RegexOptions.None);
        return (fns, refs, ext, dde, toks.Any(t => t.Kind == K.Array));
    }

    /// <summary>Classification of one formula for the inventory (before any conversion).</summary>
    public static List<string> Classify(string formula, bool workbookHasMacros, out string? warning)
    {
        warning = null;
        var classes = new List<string>();
        var (fns, _, ext, dde, array) = Scan(formula);
        if (ext || dde) { classes.Add(FormulaClass.External); warning = dde ? "Dynamic data exchange (DDE) link; never run." : "Refers to another workbook; external links are never followed."; }
        var udf = fns.Where(f => f.Contains('.') && !f.Contains("DIST") && !f.Contains("INV") && f is not ("STDEV.S" or "STDEV.P" or "VAR.S" or "VAR.P" or "CEILING.MATH" or "FLOOR.MATH" or "RANK.EQ" or "PERCENTILE.INC" or "T.INV.2T")).ToList();
        if (fns.Any(MacroFunctions.Contains) || (workbookHasMacros && udf.Count > 0)) { classes.Add(FormulaClass.Macro); warning ??= "Depends on macro code or a macro function; never run."; }
        if (fns.Any(Volatile.Contains)) classes.Add(FormulaClass.Volatile);
        if (fns.Any(PreservedOnly.Contains) || array) { classes.Add(FormulaClass.Preserved); warning ??= array ? "Array constant; the result stored in the file is shown." : $"{string.Join(", ", fns.Where(PreservedOnly.Contains))}: the result stored in the file is shown until the cell is recalculated."; }
        if (fns.Any(f => f is "OFFSET" or "INDIRECT")) classes.Add(FormulaClass.Review);
        if (!classes.Any(c => c is FormulaClass.External or FormulaClass.Macro or FormulaClass.Preserved)) classes.Insert(0, FormulaClass.Server);
        return classes;
    }
}
