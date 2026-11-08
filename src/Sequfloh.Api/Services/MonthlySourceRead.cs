using System.Globalization;
using ClosedXML.Excel;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>Shared reading rules for the monthly workbooks: US dates, money, null markers, and headers kept in column order.</summary>
public static class MonthlySourceRead
{
    public const int WarningCap = 80;

    public static bool IsNullMarker(string? raw)
    {
        var s = (raw ?? "").Trim();
        return s.Length == 0 || s == "/" || s.Equals("n/a", StringComparison.OrdinalIgnoreCase) || s.Equals("#n/a", StringComparison.OrdinalIgnoreCase);
    }

    public static string Norm(string? header)
    {
        var s = (header ?? "").Trim().ToLowerInvariant();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s;
    }

    /// <summary>US month-first dates. Two-digit years follow the invariant calendar (26 is 2026). "/", N/A, #N/A and blanks are null.</summary>
    public static DateOnly? ParseUsDate(string? raw, out string why)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) { why = "the date is empty"; return null; }
        if (IsNullMarker(s)) { why = $"\"{s}\" is not a date"; return null; }
        var formats = new[] { "M/d/yyyy", "M/d/yy", "MM/dd/yyyy", "MM/dd/yy", "M-d-yyyy", "M-d-yy", "MM-dd-yyyy", "MM-dd-yy" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            why = "";
            return DateOnly.FromDateTime(dt);
        }
        why = $"\"{s}\" is not a US date (month/day/year)";
        return null;
    }

    /// <summary>Currency with optional $ and thousands separators. Parentheses and a leading minus stay negative. Null markers are null, not zero.</summary>
    public static decimal? ParseMoney(string? raw, out string why)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) { why = "the amount is empty"; return null; }
        if (IsNullMarker(s)) { why = $"\"{s}\" is not an amount"; return null; }
        var neg = s.StartsWith('(') && s.EndsWith(')');
        s = s.Trim().Trim('(', ')').Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d))
        {
            why = $"\"{raw!.Trim()}\" is not an amount";
            return null;
        }
        why = "";
        return neg ? -Math.Abs(d) : d;
    }

    public static decimal? ParseNumber(string? raw, out string why)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) { why = "the number is empty"; return null; }
        if (IsNullMarker(s)) { why = $"\"{s}\" is not a number"; return null; }
        s = s.Replace(",", "", StringComparison.Ordinal);
        if (!decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d))
        {
            why = $"\"{raw!.Trim()}\" is not a number";
            return null;
        }
        why = "";
        return d;
    }

    public static XLWorkbook Open(Stream content)
    {
        try
        {
            var ms = new MemoryStream();
            content.CopyTo(ms);
            ms.Position = 0;
            return new XLWorkbook(ms);
        }
        catch (Exception)
        {
            throw new AppException("The workbook could not be read. Use an .xlsx file.");
        }
    }

    public static IXLWorksheet? Sheet(XLWorkbook wb, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal));
            if (hit is not null) return hit;
        }
        foreach (var name in names)
        {
            var hit = wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Header cells from column A through the last used cell, including blanks, in order. Nothing is merged or dropped.</summary>
    public static List<string> Headers(IXLWorksheet sheet, int headerRow)
    {
        var last = sheet.Row(headerRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
        var list = new List<string>(last);
        for (var c = 1; c <= last; c++) list.Add(CellText(sheet.Cell(headerRow, c)));
        return list;
    }

    public static int? Find(IReadOnlyList<string> headers, params string[] aliases)
    {
        var want = aliases.Select(Norm).Where(a => a.Length > 0).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            var n = Norm(headers[i]);
            if (n.Length > 0 && want.Contains(n)) return i;
        }
        return null;
    }

    /// <summary>The nth column whose heading matches, so two Notes columns stay two columns.</summary>
    public static int? FindNth(IReadOnlyList<string> headers, int nth, params string[] aliases)
    {
        var want = aliases.Select(Norm).Where(a => a.Length > 0).ToHashSet(StringComparer.Ordinal);
        var seen = 0;
        for (var i = 0; i < headers.Count; i++)
        {
            var n = Norm(headers[i]);
            if (n.Length == 0 || !want.Contains(n)) continue;
            if (seen == nth) return i;
            seen++;
        }
        return null;
    }

    public static string CellText(IXLCell c)
    {
        if (c.IsEmpty()) return "";
        try
        {
            return c.DataType switch
            {
                XLDataType.DateTime => c.GetDateTime().ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                XLDataType.Number => NumberText(c),
                XLDataType.Boolean => c.GetBoolean() ? "TRUE" : "FALSE",
                XLDataType.TimeSpan => c.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
                _ => c.GetString(),
            };
        }
        catch (Exception)
        {
            return c.GetFormattedString();
        }
    }

    static string NumberText(IXLCell c)
    {
        var n = c.GetDouble();
        var fmt = c.Style.NumberFormat.Format ?? "";
        if (IsDateFormat(fmt) && n is > 0 and < 100000)
            return DateTime.FromOADate(n).ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        return n.ToString("0.########", CultureInfo.InvariantCulture);
    }

    static bool IsDateFormat(string fmt)
    {
        if (string.IsNullOrWhiteSpace(fmt) || fmt.Equals("General", StringComparison.OrdinalIgnoreCase)) return false;
        var f = fmt.ToLowerInvariant();
        return f.Contains('y') && (f.Contains('d') || f.Contains('m'));
    }

    /// <summary>A date column. Numeric Excel dates are accepted even when the cell format is General. Other numbers are rejected.</summary>
    public static DateOnly? ReadDate(IXLCell c, out string why)
    {
        if (c.IsEmpty()) { why = "the date is empty"; return null; }
        if (c.DataType == XLDataType.DateTime)
        {
            why = "";
            return DateOnly.FromDateTime(c.GetDateTime().Date);
        }
        if (c.DataType == XLDataType.Number)
        {
            var n = c.GetDouble();
            if (n is > 20000 and < 80000)
            {
                why = "";
                return DateOnly.FromDateTime(DateTime.FromOADate(n).Date);
            }
            why = $"\"{n.ToString(CultureInfo.InvariantCulture)}\" is not a date";
            return null;
        }
        return ParseUsDate(CellText(c), out why);
    }

    public static bool InMonth(DateOnly date, DateOnly start, DateOnly end) => date >= start && date < end;

    public static bool RowHasIdentity(IXLWorksheet sheet, int row, int? docCol, int? partCol)
    {
        if (docCol is int d && CellText(sheet.Cell(row, d + 1)).Trim().Length > 0) return true;
        if (partCol is int p && CellText(sheet.Cell(row, p + 1)).Trim().Length > 0) return true;
        return false;
    }

    public static bool RowBlank(IXLWorksheet sheet, int row, int columns)
    {
        var width = Math.Max(columns, 1);
        for (var c = 1; c <= width; c++)
            if (!string.IsNullOrWhiteSpace(CellText(sheet.Cell(row, c)))) return false;
        return true;
    }

    public static void Add(List<string> warnings, string message)
    {
        if (warnings.Count < WarningCap) warnings.Add(message);
    }

    public static void AddSkip(List<SkippedRow> skipped, List<string> warnings, ref int total, string tab, int row, string reason)
    {
        total++;
        if (skipped.Count < WarningCap) skipped.Add(new SkippedRow(tab, row, reason));
        Add(warnings, $"{tab} row {row}: {reason}. The row was not counted.");
    }
}

public sealed record SkippedRow(string Tab, int Row, string Reason);
public sealed record TabRead(string Tab, int HeaderRow, IReadOnlyList<string> Headers);

public sealed class SourceImport
{
    public string FileType { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Applied { get; init; }
    /// <summary>Shown when the file is stored and no fields are filled.</summary>
    public string Note { get; init; } = "";
    public string Tab { get; init; } = "";
    public List<TabRead> Tabs { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<SkippedRow> Skipped { get; init; } = [];
    public int SkippedTotal { get; init; }
    public int IncludedRows { get; init; }
    public int? DistinctCount { get; init; }
    public int OutsideMonth { get; init; }
    /// <summary>Canonical text of each value this file computed. Absent keys are left alone.</summary>
    public Dictionary<string, string> Fields { get; init; } = new();
    /// <summary>Worksheet that produced a field, when it is not <see cref="Tab"/>.</summary>
    public Dictionary<string, string> FieldTabs { get; init; } = new();
    public List<FaiRow> FirstArticle { get; init; } = [];
    public List<PartClaimRow> TopParts { get; init; } = [];
    public List<NamedCount> Vehicles { get; init; } = [];
}
