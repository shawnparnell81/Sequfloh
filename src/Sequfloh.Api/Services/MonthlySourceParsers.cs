using System.Globalization;
using ClosedXML.Excel;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>One uploaded file type. Ready parsers fill report fields. The others store the file and leave the fields to be typed.</summary>
public interface IMonthlySourceParser
{
    string Type { get; }
    string Label { get; }
    string Hint { get; }
    bool Ready { get; }
    IReadOnlyList<string> Extensions { get; }
    /// <summary>Report sections this file is expected to fill.</summary>
    IReadOnlyList<string> Fills { get; }
    SourceImport Read(Stream content, int year, int month);
}

public static class MonthlySourceCatalog
{
    public static readonly IMonthlySourceParser[] All =
    [
        new LaborClaimsParser(),
        new ProductAlertsParser(),
        new EngineeringFaiParser(),
        new RawLaborExportParser(),
        new PlaceholderParser("ncr-log", "NCR, CAR, PIR and RPN log",
            "Stored only. The NCR log parser is not in place yet, so section 6.6 stays manual.",
            [".xlsx", ".xlsm", ".csv"],
            ["6.6 NCRs, CARs, PIRs, & RPNs"]),
    ];

    public static IMonthlySourceParser? Find(string? type) =>
        All.FirstOrDefault(p => string.Equals(p.Type, type?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Labor claims tracker. Sheet1 is the only claim tab. Column A (no heading) is the month date.
/// Approved and Denied come from Conclusion. Labor Cost and Amount DMA authorized are not treated as amounts requested.
/// </summary>
public sealed class LaborClaimsParser : IMonthlySourceParser
{
    public string Type => "labor-claims";
    public string Label => "Labor claims tracker";
    public string Hint => "The internal claims workbook. Sheet1 is the tracker. A raw-export tab such as Sheet11 or Received fills the raw count, amounts, time to failure, top parts and vehicle models for this month only.";
    public bool Ready => true;
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];
    public IReadOnlyList<string> Fills =>
    [
        "6.1 Total Claims (tracker processed)",
        "6.1 Approved and Denied",
        "6.1 Fuel pumps returned, DMA and Carter / Non-DMA, when the part crosswalk marks the part as a fuel pump",
        "6.1 raw claim count and amounts requested, from a raw-export tab in this workbook",
        "6.2 time to failure, top returning parts and vehicle models, from a raw-export tab in this workbook",
    ];

    public SourceImport Read(Stream content, int year, int month)
    {
        using var wb = MonthlySourceRead.Open(content);
        var sheet = MonthlySourceRead.Sheet(wb, "Sheet1") ?? throw new AppException("The labor claims workbook has no Sheet1 tab.");
        const int headerRow = 2;
        var headers = MonthlySourceRead.Headers(sheet, headerRow);
        var claimCol = MonthlySourceRead.Find(headers, "claim#", "claim #", "claim number")
            ?? throw new AppException("Sheet1 row 2 does not have a claim# column.");
        var partCol = MonthlySourceRead.Find(headers, "part#", "part #", "part number");
        var conclusionCol = MonthlySourceRead.Find(headers, "conclusion");
        var dmaCol = MonthlySourceRead.Find(headers, "dma part (y/n)", "dma part");
        var pumps = FuelPumps(wb);
        var (start, end) = Month(year, month);
        var warnings = new List<string>();
        var skipped = new List<SkippedRow>();
        var skippedTotal = 0;
        var included = 0;
        var outside = 0;
        var approved = 0;
        var denied = 0;
        var blank = 0;
        var unknown = 0;
        var dma = 0;
        var nonDma = 0;
        var pumpRows = 0;
        var otherFlag = 0;
        var claims = new HashSet<string>(StringComparer.Ordinal);
        var last = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        for (var r = headerRow + 1; r <= last; r++)
        {
            if (MonthlySourceRead.RowBlank(sheet, r, headers.Count)) continue;
            var date = MonthlySourceRead.ReadDate(sheet.Cell(r, 1), out var why);
            if (date is null)
            {
                MonthlySourceRead.AddSkip(skipped, warnings, ref skippedTotal, "Sheet1", r, why);
                continue;
            }
            if (!MonthlySourceRead.InMonth(date.Value, start, end)) { outside++; continue; }
            var claim = MonthlySourceRead.CellText(sheet.Cell(r, claimCol + 1)).Trim();
            if (claim.Length == 0 || MonthlySourceRead.IsNullMarker(claim))
            {
                MonthlySourceRead.AddSkip(skipped, warnings, ref skippedTotal, "Sheet1", r, "claim# is empty");
                continue;
            }
            included++;
            claims.Add(claim);
            if (conclusionCol is int cc)
            {
                var kind = ConclusionKind(MonthlySourceRead.CellText(sheet.Cell(r, cc + 1)));
                if (kind == "approved") approved++;
                else if (kind == "denied") denied++;
                else if (kind == "blank") blank++;
                else
                {
                    unknown++;
                    MonthlySourceRead.Add(warnings, $"Sheet1 row {r}: conclusion \"{MonthlySourceRead.CellText(sheet.Cell(r, cc + 1)).Trim()}\" is not Approved, Accepted or Denied, so it was not counted as either.");
                }
            }
            if (pumps.Count > 0 && partCol is int pc)
            {
                var part = MonthlySourceRead.CellText(sheet.Cell(r, pc + 1)).Trim();
                if (pumps.Contains(part))
                {
                    pumpRows++;
                    var flag = dmaCol is int dc ? MonthlySourceRead.CellText(sheet.Cell(r, dc + 1)).Trim() : "";
                    if (flag.Equals("Y", StringComparison.OrdinalIgnoreCase)) dma++;
                    else if (flag.Equals("N", StringComparison.OrdinalIgnoreCase)) nonDma++;
                    else
                    {
                        otherFlag++;
                        MonthlySourceRead.Add(warnings, $"Sheet1 row {r}: DMA Part (Y/N) is \"{flag}\". Only Y and N are counted in the fuel pump split.");
                    }
                }
            }
        }
        var raw = RawExportReader.Read(wb, year, month);
        foreach (var w in wb.Worksheets)
        {
            if (w == sheet || IsCrosswalk(w.Name) || raw.TabNames.Contains(w.Name) || w.LastRowUsed() is null) continue;
            MonthlySourceRead.Add(warnings, $"Tab \"{w.Name}\" was not included. Monthly claim counts use Sheet1 only.");
        }
        warnings.AddRange(raw.Warnings);
        skipped.AddRange(raw.Skipped);
        skippedTotal += raw.SkippedTotal;
        if (pumps.Count == 0)
            MonthlySourceRead.Add(warnings, CrosswalkSheet(wb) is null
                ? "No DMA part crosswalk tab was found, so fuel pumps were not split."
                : "The DMA part crosswalk has no part in the Fuel Pump category, so DMA and Carter / Non-DMA counts were not filled.");
        else if (pumpRows == 0)
            MonthlySourceRead.Add(warnings, "No Sheet1 part in this month is a Fuel Pump on the crosswalk, so DMA and Carter / Non-DMA counts were not filled.");
        if (blank > 0)
            MonthlySourceRead.Add(warnings, $"{blank} conclusion {(blank == 1 ? "cell was" : "cells were")} blank. Blank is not counted as pending.");
        if (unknown > 0)
            MonthlySourceRead.Add(warnings, $"{unknown} conclusion {(unknown == 1 ? "value was" : "values were")} left out of Approved and Denied.");
        MonthlySourceRead.Add(warnings, outside == 0
            ? $"Sheet1: {included} {(included == 1 ? "row" : "rows")} in {MonthName(month)} {year}, {claims.Count} distinct claim {(claims.Count == 1 ? "number" : "numbers")}."
            : $"Sheet1: {included} {(included == 1 ? "row" : "rows")} in {MonthName(month)} {year}, {claims.Count} distinct claim {(claims.Count == 1 ? "number" : "numbers")}. {outside} {(outside == 1 ? "row was" : "rows were")} in another month and {(outside == 1 ? "was" : "were")} not counted.");
        if (included != claims.Count)
            MonthlySourceRead.Add(warnings, "Tracker processed is the row count. Rows were not collapsed by claim number or part number.");
        var fields = new Dictionary<string, string>
        {
            [MonthlyReportFields.TotalClaimsProcessed] = included.ToString(CultureInfo.InvariantCulture),
            [MonthlyReportFields.ClaimsApproved] = approved.ToString(CultureInfo.InvariantCulture),
            [MonthlyReportFields.ClaimsDenied] = denied.ToString(CultureInfo.InvariantCulture),
        };
        if (pumpRows > 0)
        {
            fields[MonthlyReportFields.DmaParts] = dma.ToString(CultureInfo.InvariantCulture);
            fields[MonthlyReportFields.CarterParts] = nonDma.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var (key, canon) in raw.Fields) fields[key] = canon;
        var tabs = new List<TabRead> { new("Sheet1", headerRow, headers) };
        tabs.AddRange(raw.Tabs);
        return new SourceImport
        {
            FileType = Type, Label = Label, Applied = true, Tab = "Sheet1",
            Tabs = tabs,
            Warnings = warnings, Skipped = skipped, SkippedTotal = skippedTotal,
            IncludedRows = included, DistinctCount = claims.Count, OutsideMonth = outside, Fields = fields,
            FieldTabs = raw.FieldTabs, TopParts = raw.TopParts, Vehicles = raw.Vehicles,
        };
    }

    static HashSet<string> FuelPumps(XLWorkbook wb)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sheet = CrosswalkSheet(wb);
        if (sheet is null) return set;
        var headers = MonthlySourceRead.Headers(sheet, 1);
        var partCol = MonthlySourceRead.Find(headers, "part number", "part#", "part #");
        var catCol = MonthlySourceRead.Find(headers, "product category", "category");
        if (partCol is null || catCol is null) return set;
        var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            var cat = MonthlySourceRead.CellText(sheet.Cell(r, catCol.Value + 1)).Trim();
            if (!cat.Equals("Fuel Pump", StringComparison.OrdinalIgnoreCase)) continue;
            var part = MonthlySourceRead.CellText(sheet.Cell(r, partCol.Value + 1)).Trim();
            if (part.Length > 0) set.Add(part);
        }
        return set;
    }

    static IXLWorksheet? CrosswalkSheet(XLWorkbook wb) => MonthlySourceRead.Sheet(wb, "DMA PART???", "DMA PART___");
    static bool IsCrosswalk(string name) => name.Equals("DMA PART???", StringComparison.OrdinalIgnoreCase) || name.Equals("DMA PART___", StringComparison.OrdinalIgnoreCase);

    /// <summary>Approved and Accepted only, after trim, ignoring case. Anything else, including "Approved 1", is unknown.</summary>
    public static string ConclusionKind(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || MonthlySourceRead.IsNullMarker(s)) return "blank";
        if (s.Equals("approved", StringComparison.OrdinalIgnoreCase) || s.Equals("accepted", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (s.Equals("denied", StringComparison.OrdinalIgnoreCase)) return "denied";
        return "unknown";
    }

    internal static (DateOnly Start, DateOnly End) Month(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        return (start, start.AddMonths(1));
    }

    internal static string MonthName(int month) => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
}

/// <summary>O'Reilly product alert tracker. Only the Oreilly tab is counted. Sheet4 and the helper tabs are not added in.</summary>
public sealed class ProductAlertsParser : IMonthlySourceParser
{
    public string Type => "product-alerts";
    public string Label => "O'Reilly product alert tracker";
    public string Hint => "Product alerts. The Oreilly tab is counted. A received date in the month sets the total; a completed date in the month sets the average time to close.";
    public bool Ready => true;
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];
    public IReadOnlyList<string> Fills => ["6.3 Total, Open, Hours Spent Investigating and average time to close"];

    public SourceImport Read(Stream content, int year, int month)
    {
        using var wb = MonthlySourceRead.Open(content);
        var sheet = MonthlySourceRead.Sheet(wb, "Oreilly", "O'Reilly") ?? throw new AppException("The product alert workbook has no Oreilly tab.");
        const int headerRow = 1;
        var headers = MonthlySourceRead.Headers(sheet, headerRow);
        var idCol = MonthlySourceRead.Find(headers, "product alert#", "product alert #", "product alert")
            ?? throw new AppException("The Oreilly tab does not have a Product Alert# column.");
        var receivedCol = MonthlySourceRead.Find(headers, "date received") ?? throw new AppException("The Oreilly tab does not have a Date Received column.");
        var hoursCol = MonthlySourceRead.Find(headers, "time spent investigating (hours)", "hours spent investigating");
        var statusCol = MonthlySourceRead.Find(headers, "status");
        var completedCol = MonthlySourceRead.Find(headers, "date completed") ?? throw new AppException("The Oreilly tab does not have a Date Completed column.");
        var daysCol = MonthlySourceRead.Find(headers, "time for completion (days)", "time for completion");
        var (start, end) = LaborClaimsParser.Month(year, month);
        var warnings = new List<string>();
        var skipped = new List<SkippedRow>();
        var skippedTotal = 0;
        var incoming = 0;
        var open = 0;
        var outside = 0;
        decimal hours = 0;
        var hourRows = 0;
        var closures = 0;
        decimal storedDays = 0;
        var storedRows = 0;
        decimal calendarDays = 0;
        var calendarRows = 0;
        var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var last = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        for (var r = headerRow + 1; r <= last; r++)
        {
            if (MonthlySourceRead.RowBlank(sheet, r, headers.Count)) continue;
            var receivedText = MonthlySourceRead.CellText(sheet.Cell(r, receivedCol + 1));
            var completedText = MonthlySourceRead.CellText(sheet.Cell(r, completedCol + 1));
            var received = MonthlySourceRead.ReadDate(sheet.Cell(r, receivedCol + 1), out var receivedWhy);
            var completed = MonthlySourceRead.ReadDate(sheet.Cell(r, completedCol + 1), out _);
            var id = MonthlySourceRead.CellText(sheet.Cell(r, idCol + 1)).Trim();
            var receivedIn = received is { } rd && MonthlySourceRead.InMonth(rd, start, end);
            var completedIn = completed is { } cd && MonthlySourceRead.InMonth(cd, start, end);
            if (received is not null && !receivedIn) outside++;
            if (!receivedIn && !completedIn)
            {
                // Another month is excluded by the date, not reported as a bad row.
                if (received is null && (id.Length > 0 || receivedText.Trim().Length > 0))
                    MonthlySourceRead.AddSkip(skipped, warnings, ref skippedTotal, sheet.Name, r, receivedWhy);
                continue;
            }
            if (receivedIn)
            {
                if (id.Length == 0 || MonthlySourceRead.IsNullMarker(id))
                    MonthlySourceRead.AddSkip(skipped, warnings, ref skippedTotal, sheet.Name, r, "Product Alert# is empty");
                else
                {
                    incoming++;
                    var badCompleted = completed is null && completedText.Trim().Length > 0 && !MonthlySourceRead.IsNullMarker(completedText);
                    if (badCompleted)
                        MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: Date Completed \"{completedText.Trim()}\" could not be read, so the alert was not counted as open or closed.");
                    else if (completed is null || completed.Value >= end) open++;
                    if (hoursCol is int hc)
                    {
                        var h = MonthlySourceRead.ParseNumber(MonthlySourceRead.CellText(sheet.Cell(r, hc + 1)), out var hw);
                        if (h is { } hv) { hours += hv; hourRows++; }
                        else if (!MonthlySourceRead.IsNullMarker(MonthlySourceRead.CellText(sheet.Cell(r, hc + 1))))
                            MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: hours {hw}. The hours were left out of the sum, not counted as zero.");
                    }
                    if (statusCol is int sc)
                    {
                        var status = MonthlySourceRead.CellText(sheet.Cell(r, sc + 1)).Trim();
                        if (status.Length > 0 && !MonthlySourceRead.IsNullMarker(status))
                            statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
                    }
                }
            }
            if (completedIn && daysCol is int dc)
            {
                closures++;
                var stored = MonthlySourceRead.ParseNumber(MonthlySourceRead.CellText(sheet.Cell(r, dc + 1)), out _);
                if (stored is { } sv) { storedDays += sv; storedRows++; }
                if (received is { } from && completed is { } to)
                {
                    var span = to.DayNumber - from.DayNumber;
                    calendarDays += span;
                    calendarRows++;
                    if (stored is { } check && check != span)
                        MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: stored time to close is {check.ToString(CultureInfo.InvariantCulture)} and the calendar difference is {span}. The stored number was kept.");
                }
            }
        }
        foreach (var w in wb.Worksheets)
        {
            if (w == sheet || w.LastRowUsed() is null) continue;
            MonthlySourceRead.Add(warnings, $"Tab \"{w.Name}\" was not included. Product alert totals use the {sheet.Name} tab only.");
        }
        if (statusCounts.Count > 0)
            MonthlySourceRead.Add(warnings, "Status on alerts received this month: " + string.Join(", ", statusCounts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Value} {p.Key}")) + ".");
        if (storedRows > 0 && calendarRows > 0)
        {
            var storedAvg = storedDays / storedRows;
            var calendarAvg = calendarDays / calendarRows;
            if (Math.Round(storedAvg, 2) != Math.Round(calendarAvg, 2))
                MonthlySourceRead.Add(warnings, $"Average time to close uses the stored days column ({Round2(storedAvg)}). The calendar average is {Round2(calendarAvg)}.");
        }
        if (hourRows < incoming)
            MonthlySourceRead.Add(warnings, $"{incoming - hourRows} received {(incoming - hourRows == 1 ? "alert has" : "alerts have")} no hours. Blank hours are left out, not added as zero.");
        MonthlySourceRead.Add(warnings, $"Oreilly: {incoming} received in {LaborClaimsParser.MonthName(month)} {year}, {open} still open at month end, {closures} completed in the month.");
        var fields = new Dictionary<string, string>
        {
            [MonthlyReportFields.AlertsTotal] = incoming.ToString(CultureInfo.InvariantCulture),
            [MonthlyReportFields.AlertsOpen] = open.ToString(CultureInfo.InvariantCulture),
        };
        if (hourRows > 0) fields[MonthlyReportFields.HoursInvestigating] = MonthlyReportMath.Canon(hours);
        if (storedRows > 0) fields[MonthlyReportFields.AvgDaysToClose] = MonthlyReportMath.Canon(storedDays / storedRows);
        return new SourceImport
        {
            FileType = Type, Label = Label, Applied = true, Tab = sheet.Name,
            Tabs = [new TabRead(sheet.Name, headerRow, headers)],
            Warnings = warnings, Skipped = skipped, SkippedTotal = skippedTotal,
            IncludedRows = incoming, DistinctCount = incoming, OutsideMonth = outside, Fields = fields,
        };
    }

    static string Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>
/// Engineering FAI tracker. Leeroy's Tab, Sammy G's tab and Fuel are first-article completions.
/// A second FAI date is a separate event and is not added to the first-article counts. Sample quantity is not used as a weight.
/// </summary>
public sealed class EngineeringFaiParser : IMonthlySourceParser
{
    public string Type => "engineering-fai";
    public string Label => "Engineering FAI tracker";
    public string Hint => "First article completions from Leeroy's Tab, Sammy G's tab and Fuel. A second FAI is counted apart from the first.";
    public bool Ready => true;
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];
    public IReadOnlyList<string> Fills => ["6.5 First Article Verifications"];

    public static readonly Dictionary<string, string> FuelCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fuel injector"] = "Fuel & Lift Supports",
        ["fuel injectors"] = "Fuel & Lift Supports",
        ["fuel pump"] = "Fuel & Lift Supports",
        ["fuel pumps"] = "Fuel & Lift Supports",
        ["gas lift support"] = "Fuel & Lift Supports",
        ["gas lift supports"] = "Fuel & Lift Supports",
        ["electric lift support"] = "Fuel & Lift Supports",
        ["electric lift supports"] = "Fuel & Lift Supports",
    };

    public static readonly Dictionary<string, string> SammyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brake wear sensor"] = "Brake Wear Sensors",
        ["brake wear sensors"] = "Brake Wear Sensors",
        ["gas lift support"] = "Fuel & Lift Supports",
        ["gas lift supports"] = "Fuel & Lift Supports",
        ["electric lift support"] = "Fuel & Lift Supports",
        ["electric lift supports"] = "Fuel & Lift Supports",
    };

    public SourceImport Read(Stream content, int year, int month)
    {
        using var wb = MonthlySourceRead.Open(content);
        var tabs = new (string Name, int HeaderRow, string Date, string Disp, string? Type, string? Doc, string? Part, string? Drawing, string? Second, string? Fixed, Dictionary<string, string>? Map)[]
        {
            ("Leeroy's Tab", 3, "completed date", "disposition", null, null, "part #", "drawing", "2nd fai completion date", "CSAs & Shocks", null),
            ("Sammy G's tab", 3, "1st fai completion date", "1st fai disposition", "part type", "validation document", "dma pn", "drawing pn", "2nd fai completion date", null, SammyCategories),
            ("Fuel", 1, "1st fai completion date", "1st fai disposition", "part type", "validation document", "pn", "drawing pn", "2nd fai completion date", null, FuelCategories),
        };
        var found = 0;
        var warnings = new List<string>();
        var skipped = new List<SkippedRow>();
        var skippedTotal = 0;
        var outside = 0;
        var second = 0;
        var reads = new List<TabRead>();
        var buckets = new Dictionary<string, FaiRow>(StringComparer.OrdinalIgnoreCase);
        var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (start, end) = LaborClaimsParser.Month(year, month);
        foreach (var spec in tabs)
        {
            var sheet = MonthlySourceRead.Sheet(wb, spec.Name);
            if (sheet is null)
            {
                MonthlySourceRead.Add(warnings, $"Tab \"{spec.Name}\" was not in the workbook.");
                continue;
            }
            found++;
            var headers = MonthlySourceRead.Headers(sheet, spec.HeaderRow);
            reads.Add(new TabRead(sheet.Name, spec.HeaderRow, headers));
            var dateCol = MonthlySourceRead.Find(headers, spec.Date) ?? throw new AppException($"{sheet.Name} row {spec.HeaderRow} does not have a {spec.Date} column.");
            var dispCol = MonthlySourceRead.Find(headers, spec.Disp) ?? throw new AppException($"{sheet.Name} row {spec.HeaderRow} does not have a {spec.Disp} column.");
            var typeCol = spec.Type is null ? null : MonthlySourceRead.Find(headers, spec.Type);
            var docCol = spec.Doc is null ? null : MonthlySourceRead.Find(headers, spec.Doc);
            var partCol = spec.Part is null ? null : MonthlySourceRead.Find(headers, spec.Part);
            var drawingCol = spec.Drawing is null ? null : MonthlySourceRead.Find(headers, spec.Drawing);
            var secondCol = spec.Second is null ? null : MonthlySourceRead.Find(headers, spec.Second);
            var last = sheet.LastRowUsed()?.RowNumber() ?? spec.HeaderRow;
            for (var r = spec.HeaderRow + 1; r <= last; r++)
            {
                if (MonthlySourceRead.RowBlank(sheet, r, headers.Count)) continue;
                if (secondCol is int sc)
                {
                    var secondDate = MonthlySourceRead.ReadDate(sheet.Cell(r, sc + 1), out _);
                    if (secondDate is { } sd && MonthlySourceRead.InMonth(sd, start, end)) second++;
                }
                var date = MonthlySourceRead.ReadDate(sheet.Cell(r, dateCol + 1), out var why);
                var dateText = MonthlySourceRead.CellText(sheet.Cell(r, dateCol + 1));
                if (date is null)
                {
                    if (!MonthlySourceRead.IsNullMarker(dateText) || dateText.Trim().Length > 0)
                    {
                        if (MonthlySourceRead.RowHasIdentity(sheet, r, docCol, partCol))
                            MonthlySourceRead.AddSkip(skipped, warnings, ref skippedTotal, sheet.Name, r, why);
                    }
                    continue;
                }
                if (!MonthlySourceRead.InMonth(date.Value, start, end)) { outside++; continue; }
                var doc = docCol is int dci ? MonthlySourceRead.CellText(sheet.Cell(r, dci + 1)).Trim() : "";
                if (doc.Length > 0)
                {
                    if (!seenDocs.Add(doc))
                        MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: validation {doc} is also on another row. Both rows were counted.");
                }
                var category = spec.Fixed;
                if (category is null)
                {
                    var partType = typeCol is int tc ? MonthlySourceRead.CellText(sheet.Cell(r, tc + 1)).Trim() : "";
                    if (partType.Length == 0 || spec.Map is null || !spec.Map.TryGetValue(partType, out category!))
                    {
                        category = partType.Length == 0 ? "Unassigned" : partType;
                        MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: part type \"{category}\" is not in the reviewed category list, so it was left as its own row.");
                    }
                }
                var disp = Disposition(MonthlySourceRead.CellText(sheet.Cell(r, dispCol + 1)));
                if (disp == "unknown")
                    MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: disposition \"{MonthlySourceRead.CellText(sheet.Cell(r, dispCol + 1)).Trim()}\" was not counted as passed, failed or passed with deviation.");
                if (!buckets.TryGetValue(category, out var row))
                    buckets[category] = row = new FaiRow { Category = category };
                row.TotalCompleted++;
                if (disp == "passed") row.Passed++;
                else if (disp == "failed") row.Failed++;
                else if (disp == "deviation") row.PassedWithDeviation++;
                else if (disp == "blank" || disp == "unresolved")
                    MonthlySourceRead.Add(warnings, $"{sheet.Name} row {r}: disposition is {(disp == "blank" ? "blank" : "Needs Special Attention")}. It counts as completed and not as passed or failed.");
            }
        }
        if (found == 0) throw new AppException("The FAI workbook has none of Leeroy's Tab, Sammy G's tab or Fuel.");
        foreach (var w in wb.Worksheets)
        {
            if (tabs.Any(t => string.Equals(t.Name, w.Name, StringComparison.OrdinalIgnoreCase)) || w.LastRowUsed() is null) continue;
            MonthlySourceRead.Add(warnings, $"Tab \"{w.Name}\" was not included. First article counts use Leeroy's Tab, Sammy G's tab and Fuel only.");
        }
        if (second > 0)
            MonthlySourceRead.Add(warnings, $"{second} second FAI {(second == 1 ? "completion was" : "completions were")} in {LaborClaimsParser.MonthName(month)} {year} and {(second == 1 ? "was" : "were")} not added to the first article totals.");
        var rows = buckets.Values.OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase).ToList();
        var fields = new Dictionary<string, string>();
        if (rows.Count > 0) fields[MonthlyReportFields.FirstArticle] = MonthlyReportMath.Canon(rows);
        MonthlySourceRead.Add(warnings, $"First article completions in {LaborClaimsParser.MonthName(month)} {year}: {rows.Sum(r => r.TotalCompleted)}.");
        return new SourceImport
        {
            FileType = Type, Label = Label, Applied = true, Tab = "Leeroy's Tab, Sammy G's tab, Fuel",
            Tabs = reads, Warnings = warnings, Skipped = skipped, SkippedTotal = skippedTotal,
            IncludedRows = rows.Sum(r => r.TotalCompleted), OutsideMonth = outside, Fields = fields, FirstArticle = rows,
        };
    }

    /// <summary>Pass/Passed, Fail/Failed, and Pass with Deviation. Needs Special Attention and blanks stay unresolved.</summary>
    public static string Disposition(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || MonthlySourceRead.IsNullMarker(s)) return "blank";
        var n = s.ToLowerInvariant();
        if (n is "pass" or "passed") return "passed";
        if (n is "fail" or "failed") return "failed";
        if (n is "pass with deviation" or "passed with deviation" or "passed w/ deviation" or "pass w/ deviation") return "deviation";
        if (n is "needs special attention") return "unresolved";
        return "unknown";
    }
}

/// <summary>
/// A raw O'Reilly labor export tab: Date Entered, Labor Amount Requested and Total Amount Requested.
/// Sheet11 is used when it has rows in the month. Received is the older layout and is used only when Sheet11 has none.
/// The two tabs are never added together.
/// </summary>
public static class RawExportReader
{
    public sealed class Result
    {
        public HashSet<string> TabNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TabRead> Tabs { get; init; } = [];
        public List<string> Warnings { get; init; } = [];
        public List<SkippedRow> Skipped { get; init; } = [];
        public int SkippedTotal { get; set; }
        public Dictionary<string, string> Fields { get; init; } = new();
        public Dictionary<string, string> FieldTabs { get; init; } = new();
        public List<PartClaimRow> TopParts { get; set; } = [];
        public List<NamedCount> Vehicles { get; set; } = [];
    }

    public static bool IsRaw(IReadOnlyList<string> headers) =>
        MonthlySourceRead.Find(headers, "date entered") is not null
        && MonthlySourceRead.Find(headers, "labor amount requested") is not null
        && MonthlySourceRead.Find(headers, "total amount requested") is not null;

    public static Result Read(XLWorkbook wb, int year, int month)
    {
        var candidates = new List<SheetTally>();
        foreach (var sheet in wb.Worksheets)
        {
            var headers = MonthlySourceRead.Headers(sheet, 1);
            if (!IsRaw(headers)) continue;
            candidates.Add(Tally(sheet, headers, year, month));
        }
        var result = new Result();
        foreach (var c in candidates) result.TabNames.Add(c.Name);
        if (candidates.Count == 0) return result;

        SheetTally? chosen = null;
        foreach (var preferred in new[] { "Sheet11", "Received" })
        {
            var hit = candidates.FirstOrDefault(c => c.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) && c.Included > 0);
            if (hit is not null) { chosen = hit; break; }
        }
        chosen ??= candidates.FirstOrDefault(c => c.Included > 0) ?? candidates[0];
        result.Tabs.Add(new TabRead(chosen.Name, 1, chosen.Headers));
        result.Warnings.AddRange(chosen.Warnings);
        result.Skipped.AddRange(chosen.Skipped);
        result.SkippedTotal = chosen.SkippedTotal;
        foreach (var other in candidates.Where(c => !c.Name.Equals(chosen.Name, StringComparison.OrdinalIgnoreCase)))
        {
            result.Warnings.Add(other.Included > 0
                ? $"Tab \"{other.Name}\" also has {other.Included} raw {(other.Included == 1 ? "row" : "rows")} in this month. It was not added, so the totals are not counted twice. {chosen.Name} was used."
                : $"Tab \"{other.Name}\" is a raw export layout and had no rows in this month.");
        }
        if (chosen.Included == 0)
        {
            result.Warnings.Add($"Tab \"{chosen.Name}\" is a raw export layout and had no rows in this month, so the raw totals were not filled.");
            return result;
        }

        var fields = result.Fields;
        var tab = chosen.Name;
        void Put(string key, string canon) { fields[key] = canon; result.FieldTabs[key] = tab; }
        Put(MonthlyReportFields.TotalClaimsRaw, chosen.Distinct.ToString(CultureInfo.InvariantCulture));
        if (chosen.LaborSeen) Put(MonthlyReportFields.LaborAmountRequested, MonthlyReportMath.Canon(Math.Round(chosen.LaborSum, 2, MidpointRounding.AwayFromZero)));
        if (chosen.PartsSeen) Put(MonthlyReportFields.PartsAmountRequested, MonthlyReportMath.Canon(Math.Round(chosen.PartsSum, 2, MidpointRounding.AwayFromZero)));
        if (chosen.TotalSeen) Put(MonthlyReportFields.TotalAmountRequested, MonthlyReportMath.Canon(Math.Round(chosen.TotalSum, 2, MidpointRounding.AwayFromZero)));
        var (mean, median) = MonthlyReportMath.TimeToFailure(chosen.Days);
        if (mean is not null) Put(MonthlyReportFields.MttfDays, MonthlyReportMath.Canon(mean));
        if (median is not null) Put(MonthlyReportFields.MedianTtfDays, MonthlyReportMath.Canon(median));
        result.TopParts = chosen.Parts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(10).Select(p => new PartClaimRow { PartNumber = p.Key, Description = chosen.Descriptions.GetValueOrDefault(p.Key) ?? "", TotalClaims = p.Value }).ToList();
        if (result.TopParts.Count > 0) Put(MonthlyReportFields.TopParts, MonthlyReportMath.CanonParts(result.TopParts));
        result.Vehicles = chosen.Vehicles.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(10).Select(p => new NamedCount { Name = p.Key, Count = p.Value }).ToList();
        if (result.Vehicles.Count > 0) Put(MonthlyReportFields.VehicleModels, MonthlyReportMath.CanonVehicles(result.Vehicles));
        result.Warnings.Add($"Tab \"{tab}\": {chosen.Included} raw {(chosen.Included == 1 ? "row" : "rows")} in this month, {chosen.Distinct} distinct {(chosen.Distinct == 1 ? "claim" : "claims")}. Sheet1 tracker rows were not added to this count.");
        if (chosen.Outside > 0)
            result.Warnings.Add($"Tab \"{tab}\": {chosen.Outside} {(chosen.Outside == 1 ? "row was" : "rows were")} in another month and {(chosen.Outside == 1 ? "was" : "were")} not counted.");
        return result;
    }

    static SheetTally Tally(IXLWorksheet sheet, List<string> headers, int year, int month)
    {
        var dateCol = MonthlySourceRead.Find(headers, "date entered")!.Value;
        var claimCol = MonthlySourceRead.Find(headers, "claim number", "claim#", "claim #");
        var itemCol = MonthlySourceRead.Find(headers, "item", "part#", "part number");
        var descCol = MonthlySourceRead.Find(headers, "description", "description / application");
        var laborCol = MonthlySourceRead.Find(headers, "labor amount requested");
        var partsCol = MonthlySourceRead.Find(headers, "total amount of parts", "parts amount requested");
        var totalCol = MonthlySourceRead.Find(headers, "total amount requested");
        var installCol = MonthlySourceRead.Find(headers, "original install date");
        var repairCol = MonthlySourceRead.Find(headers, "repair install date", "repair date");
        var yearCol = MonthlySourceRead.Find(headers, "year");
        var makeCol = MonthlySourceRead.Find(headers, "make");
        var modelCol = MonthlySourceRead.Find(headers, "model");
        var (start, end) = (new DateOnly(year, month, 1), new DateOnly(year, month, 1).AddMonths(1));
        var tally = new SheetTally { Name = sheet.Name, Headers = headers };
        var claims = new HashSet<string>(StringComparer.Ordinal);
        var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            if (MonthlySourceRead.RowBlank(sheet, r, headers.Count)) continue;
            var date = MonthlySourceRead.ReadDate(sheet.Cell(r, dateCol + 1), out var why);
            if (date is null)
            {
                MonthlySourceRead.AddSkip(tally.Skipped, tally.Warnings, ref tally.SkippedTotal, sheet.Name, r, why);
                continue;
            }
            if (!MonthlySourceRead.InMonth(date.Value, start, end)) { tally.Outside++; continue; }
            var claim = claimCol is int cc ? MonthlySourceRead.CellText(sheet.Cell(r, cc + 1)).Trim() : "";
            if (claim.Length == 0 || MonthlySourceRead.IsNullMarker(claim))
            {
                MonthlySourceRead.AddSkip(tally.Skipped, tally.Warnings, ref tally.SkippedTotal, sheet.Name, r, "claim number is empty");
                continue;
            }
            tally.Included++;
            claims.Add(claim);
            AddMoney(sheet, r, laborCol, tally.Warnings, sheet.Name, ref tally.LaborSum, ref tally.LaborSeen);
            AddMoney(sheet, r, partsCol, tally.Warnings, sheet.Name, ref tally.PartsSum, ref tally.PartsSeen);
            AddMoney(sheet, r, totalCol, tally.Warnings, sheet.Name, ref tally.TotalSum, ref tally.TotalSeen);
            if (installCol is int ic && repairCol is int rc)
            {
                var install = MonthlySourceRead.ReadDate(sheet.Cell(r, ic + 1), out _);
                var repair = MonthlySourceRead.ReadDate(sheet.Cell(r, rc + 1), out _);
                if (install is not null && repair is not null && repair.Value.DayNumber >= install.Value.DayNumber)
                    tally.Days.Add(repair.Value.DayNumber - install.Value.DayNumber);
            }
            if (itemCol is int icol)
            {
                var item = MonthlySourceRead.CellText(sheet.Cell(r, icol + 1)).Trim();
                if (item.Length > 0 && !MonthlySourceRead.IsNullMarker(item))
                {
                    tally.Parts[item] = tally.Parts.GetValueOrDefault(item) + 1;
                    if (descCol is int dc && !tally.Descriptions.ContainsKey(item))
                        tally.Descriptions[item] = MonthlySourceRead.CellText(sheet.Cell(r, dc + 1)).Trim();
                }
            }
            var vehicle = string.Join(' ', new[] { Text(sheet, r, yearCol), Text(sheet, r, makeCol), Text(sheet, r, modelCol) }.Where(s => s.Length > 0));
            if (vehicle.Length > 0) tally.Vehicles[vehicle] = tally.Vehicles.GetValueOrDefault(vehicle) + 1;
        }
        tally.Distinct = claims.Count;
        return tally;
    }

    static void AddMoney(IXLWorksheet sheet, int row, int? col, List<string> warnings, string tab, ref decimal sum, ref bool seen)
    {
        if (col is not int c) return;
        var text = MonthlySourceRead.CellText(sheet.Cell(row, c + 1));
        if (string.IsNullOrWhiteSpace(text) || MonthlySourceRead.IsNullMarker(text)) return;
        var money = MonthlySourceRead.ParseMoney(text, out var why);
        if (money is null)
        {
            MonthlySourceRead.Add(warnings, $"{tab} row {row}: {why}. The amount was left out of the total.");
            return;
        }
        seen = true;
        sum += money.Value;
    }

    static string Text(IXLWorksheet sheet, int row, int? col) =>
        col is int c ? MonthlySourceRead.CellText(sheet.Cell(row, c + 1)).Trim() : "";

    sealed class SheetTally
    {
        public string Name { get; init; } = "";
        public List<string> Headers { get; init; } = [];
        public int Included { get; set; }
        public int Outside { get; set; }
        public int Distinct { get; set; }
        public int SkippedTotal;
        public decimal LaborSum;
        public decimal PartsSum;
        public decimal TotalSum;
        public bool LaborSeen;
        public bool PartsSeen;
        public bool TotalSeen;
        public List<decimal> Days { get; } = [];
        public Dictionary<string, int> Parts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Descriptions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Vehicles { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
        public List<SkippedRow> Skipped { get; } = [];
    }
}

public sealed class RawLaborExportParser : IMonthlySourceParser
{
    public string Type => "oreilly-raw";
    public string Label => "O'Reilly raw labor claim export";
    public string Hint => "A raw export workbook. The tab with Date Entered and Labor Amount Requested fills the raw count, amounts, time to failure, top parts and vehicle models.";
    public bool Ready => true;
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];
    public IReadOnlyList<string> Fills =>
    [
        "6.1 raw claim count and amounts requested",
        "6.2 time to failure, top returning parts and vehicle models",
    ];

    public SourceImport Read(Stream content, int year, int month)
    {
        using var wb = MonthlySourceRead.Open(content);
        var raw = RawExportReader.Read(wb, year, month);
        if (raw.TabNames.Count == 0)
            return new SourceImport
            {
                FileType = Type, Label = Label, Applied = false,
                Note = "No raw export tab was found. The file is stored and the raw totals stay empty.",
                Warnings = ["No tab has Date Entered, Labor Amount Requested and Total Amount Requested."],
            };
        return new SourceImport
        {
            FileType = Type, Label = Label, Applied = raw.Fields.Count > 0, Tab = raw.Tabs.FirstOrDefault()?.Tab ?? "",
            Tabs = raw.Tabs, Warnings = raw.Warnings, Skipped = raw.Skipped, SkippedTotal = raw.SkippedTotal,
            IncludedRows = int.TryParse(raw.Fields.GetValueOrDefault(MonthlyReportFields.TotalClaimsRaw), out var n) ? n : 0,
            DistinctCount = int.TryParse(raw.Fields.GetValueOrDefault(MonthlyReportFields.TotalClaimsRaw), out var d) ? d : null,
            Fields = raw.Fields, FieldTabs = raw.FieldTabs, TopParts = raw.TopParts, Vehicles = raw.Vehicles,
            Note = raw.Fields.Count == 0 ? "The raw export tab had no rows in this month." : "",
        };
    }
}

public sealed class PlaceholderParser(string type, string label, string note, string[] extensions, string[] fills) : IMonthlySourceParser
{
    public string Type => type;
    public string Label => label;
    public string Hint => note;
    public bool Ready => false;
    public IReadOnlyList<string> Extensions => extensions;
    public IReadOnlyList<string> Fills => fills;
    public SourceImport Read(Stream content, int year, int month) => new()
    {
        FileType = type, Label = label, Applied = false, Note = note,
        Warnings = [note],
    };
}
