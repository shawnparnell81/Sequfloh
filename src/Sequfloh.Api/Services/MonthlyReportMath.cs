using System.Globalization;
using System.Text.Json;
using Sequfloh.Domain.Execution;

namespace Sequfloh.Infrastructure.Modules.Execution;

public static class MonthlyReportFields
{
    public const string TotalClaimsProcessed = "totalClaimsProcessed";
    public const string ClaimsApproved = "claimsApproved";
    public const string ClaimsDenied = "claimsDenied";
    public const string DmaParts = "dmaParts";
    public const string CarterParts = "carterParts";
    public const string AlertsTotal = "alertsTotal";
    public const string AlertsOpen = "alertsOpen";
    public const string HoursInvestigating = "hoursInvestigating";
    public const string AvgDaysToClose = "avgDaysToClose";
    public const string FirstArticle = "firstArticle";
    public const string TotalClaimsRaw = "totalClaimsRaw";
    public const string TotalAmountRequested = "totalAmountRequested";
    public const string PartsAmountRequested = "partsAmountRequested";
    public const string LaborAmountRequested = "laborAmountRequested";
    public const string MttfDays = "mttfDays";
    public const string MedianTtfDays = "medianTtfDays";
    public const string TopParts = "topParts";
    public const string VehicleModels = "vehicleModels";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [TotalClaimsProcessed] = "tracker processed claims",
        [ClaimsApproved] = "approved claims",
        [ClaimsDenied] = "denied claims",
        [DmaParts] = "DMA fuel pumps",
        [CarterParts] = "Carter / Non-DMA fuel pumps",
        [AlertsTotal] = "total product alerts",
        [AlertsOpen] = "open product alerts",
        [HoursInvestigating] = "hours spent investigating",
        [AvgDaysToClose] = "average time to close",
        [FirstArticle] = "first article verifications",
        [TotalClaimsRaw] = "raw export claims",
        [TotalAmountRequested] = "total amount requested",
        [PartsAmountRequested] = "parts amount requested",
        [LaborAmountRequested] = "labor amount requested",
        [MttfDays] = "mean time to failure",
        [MedianTtfDays] = "median time to failure",
        [TopParts] = "top returning parts",
        [VehicleModels] = "top vehicle models",
    };
}

public static class MonthlyReportMath
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static decimal? Liability(int? count, decimal rate) => count is null ? null : Math.Round(count.Value * rate, 2, MidpointRounding.AwayFromZero);

    /// <summary>Shown in a block or cell that has nothing for the month. Not a placeholder such as N/A.</summary>
    public const string EmptyNote = "no new data for this month";

    public static string Ratio(decimal? labor, decimal? parts)
    {
        if (labor is null || parts is null || parts == 0) return EmptyNote;
        return (labor.Value / parts.Value).ToString("0.##", Inv) + ":1";
    }

    public static string Days(decimal? days) => days is null ? EmptyNote : days.Value.ToString("0.0", Inv) + " Days";

    public static (decimal? Mean, decimal? Median) TimeToFailure(IEnumerable<decimal> days)
    {
        var list = days.Where(d => d >= 0).OrderBy(d => d).ToList();
        if (list.Count == 0) return (null, null);
        var mean = Math.Round(list.Average(), 1, MidpointRounding.AwayFromZero);
        decimal median = list.Count % 2 == 1 ? list[list.Count / 2] : Math.Round((list[list.Count / 2 - 1] + list[list.Count / 2]) / 2m, 1, MidpointRounding.AwayFromZero);
        return (mean, median);
    }

    public static FaiRow FaiTotal(IEnumerable<FaiRow> rows)
    {
        var list = rows.Where(r => !r.Category.Trim().Equals("Total", StringComparison.OrdinalIgnoreCase)).ToList();
        return new FaiRow
        {
            Category = "Total",
            TotalCompleted = list.Sum(r => r.TotalCompleted),
            Passed = list.Sum(r => r.Passed),
            Failed = list.Sum(r => r.Failed),
            PassedWithDeviation = list.Sum(r => r.PassedWithDeviation),
        };
    }

    public static string Canon(int? v) => v?.ToString(Inv) ?? "";
    public static string Canon(decimal? v) => v is null ? "" : v.Value.ToString("0.####", Inv);
    public static string Canon(IEnumerable<FaiRow> rows) =>
        string.Join("|", rows.OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Category.Trim()}\t{r.TotalCompleted}\t{r.Passed}\t{r.Failed}\t{r.PassedWithDeviation}"));

    public static string CanonOf(MonthlyReportBody b, string key) => key switch
    {
        MonthlyReportFields.TotalClaimsProcessed => Canon(b.TotalClaimsProcessed),
        MonthlyReportFields.ClaimsApproved => Canon(b.ClaimsApproved),
        MonthlyReportFields.ClaimsDenied => Canon(b.ClaimsDenied),
        MonthlyReportFields.DmaParts => Canon(b.DmaParts),
        MonthlyReportFields.CarterParts => Canon(b.CarterParts),
        MonthlyReportFields.AlertsTotal => Canon(b.AlertsTotal),
        MonthlyReportFields.AlertsOpen => Canon(b.AlertsOpen),
        MonthlyReportFields.HoursInvestigating => Canon(b.HoursInvestigating),
        MonthlyReportFields.AvgDaysToClose => Canon(b.AvgDaysToClose),
        MonthlyReportFields.FirstArticle => Canon(b.FirstArticle),
        MonthlyReportFields.TotalClaimsRaw => Canon(b.TotalClaimsRaw),
        MonthlyReportFields.TotalAmountRequested => Canon(b.TotalAmountRequested),
        MonthlyReportFields.PartsAmountRequested => Canon(b.PartsAmountRequested),
        MonthlyReportFields.LaborAmountRequested => Canon(b.LaborAmountRequested),
        MonthlyReportFields.MttfDays => Canon(b.MttfDays),
        MonthlyReportFields.MedianTtfDays => Canon(b.MedianTtfDays),
        MonthlyReportFields.TopParts => CanonParts(b.TopParts),
        MonthlyReportFields.VehicleModels => CanonVehicles(b.VehicleModels),
        _ => "",
    };

    public static string CanonParts(IEnumerable<PartClaimRow> rows) =>
        string.Join("|", rows.Select(r => $"{r.PartNumber.Trim()}\t{r.Description.Trim()}\t{r.TotalClaims}"));

    public static string CanonVehicles(IEnumerable<NamedCount> rows) =>
        string.Join("|", rows.Select(r => $"{r.Name.Trim()}\t{r.Count}"));

    public static void Assign(MonthlyReportBody b, string key, string canon, IReadOnlyList<FaiRow>? fai = null, IReadOnlyList<PartClaimRow>? parts = null, IReadOnlyList<NamedCount>? vehicles = null)
    {
        switch (key)
        {
            case MonthlyReportFields.TotalClaimsProcessed: b.TotalClaimsProcessed = ParseInt(canon); break;
            case MonthlyReportFields.ClaimsApproved: b.ClaimsApproved = ParseInt(canon); break;
            case MonthlyReportFields.ClaimsDenied: b.ClaimsDenied = ParseInt(canon); break;
            case MonthlyReportFields.DmaParts: b.DmaParts = ParseInt(canon); break;
            case MonthlyReportFields.CarterParts: b.CarterParts = ParseInt(canon); break;
            case MonthlyReportFields.AlertsTotal: b.AlertsTotal = ParseInt(canon); break;
            case MonthlyReportFields.AlertsOpen: b.AlertsOpen = ParseInt(canon); break;
            case MonthlyReportFields.HoursInvestigating: b.HoursInvestigating = ParseDec(canon); break;
            case MonthlyReportFields.AvgDaysToClose: b.AvgDaysToClose = ParseDec(canon); break;
            case MonthlyReportFields.FirstArticle: b.FirstArticle = fai?.Select(Clone).ToList() ?? []; break;
            case MonthlyReportFields.TotalClaimsRaw: b.TotalClaimsRaw = ParseInt(canon); break;
            case MonthlyReportFields.TotalAmountRequested: b.TotalAmountRequested = ParseDec(canon); break;
            case MonthlyReportFields.PartsAmountRequested: b.PartsAmountRequested = ParseDec(canon); break;
            case MonthlyReportFields.LaborAmountRequested: b.LaborAmountRequested = ParseDec(canon); break;
            case MonthlyReportFields.MttfDays: b.MttfDays = ParseDec(canon); break;
            case MonthlyReportFields.MedianTtfDays: b.MedianTtfDays = ParseDec(canon); break;
            case MonthlyReportFields.TopParts: b.TopParts = parts?.Select(p => new PartClaimRow { PartNumber = p.PartNumber, Description = p.Description, TotalClaims = p.TotalClaims }).ToList() ?? []; break;
            case MonthlyReportFields.VehicleModels: b.VehicleModels = vehicles?.Select(v => new NamedCount { Name = v.Name, Count = v.Count }).ToList() ?? []; break;
        }
    }

    static int? ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var n) ? n : null;
    static decimal? ParseDec(string s) => decimal.TryParse(s, NumberStyles.Number, Inv, out var n) ? n : null;
    static FaiRow Clone(FaiRow r) => new() { Category = r.Category, TotalCompleted = r.TotalCompleted, Passed = r.Passed, Failed = r.Failed, PassedWithDeviation = r.PassedWithDeviation };

    public static IReadOnlyList<(int Year, int Month)> Window(int year, int month, int count = 6)
    {
        var list = new List<(int, int)>();
        var y = year;
        var m = month;
        for (var i = 0; i < count; i++)
        {
            list.Add((y, m));
            m--;
            if (m == 0) { m = 12; y--; }
        }
        list.Reverse();
        return list;
    }

    public static List<TrendMonth> Trend(int year, int month, MonthlyReportBody current, IEnumerable<(int Year, int Month, MonthlyReportBody Body)> saved)
    {
        var prior = saved.ToDictionary(s => (s.Year, s.Month), s => s.Body);
        var list = new List<TrendMonth>();
        foreach (var (y, m) in Window(year, month))
        {
            var mine = y == year && m == month;
            var body = mine ? current : prior.GetValueOrDefault((y, m));
            list.Add(new TrendMonth(y, m, CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m), mine, body));
        }
        return list;
    }

    /// <summary>Copies the document-control header only. Month data is not carried forward.</summary>
    public static void CopyText(MonthlyReportBody from, MonthlyReportBody into)
    {
        into.DocumentId = string.IsNullOrWhiteSpace(from.DocumentId) ? MonthlyReportIdentity.DefaultId : from.DocumentId;
        into.DocumentRevision = string.IsNullOrWhiteSpace(from.DocumentRevision) ? MonthlyReportIdentity.DefaultRevision : from.DocumentRevision;
        into.DocumentTitle = string.IsNullOrWhiteSpace(from.DocumentTitle) ? MonthlyReportIdentity.DefaultTitle : from.DocumentTitle;
        into.ApprovedById = from.ApprovedById;
        into.ApprovedByName = from.ApprovedByName;
    }

    public static IReadOnlyList<string> ChangedSections(MonthlyReportBody before, MonthlyReportBody after, decimal rateBefore, decimal rateAfter)
    {
        var changed = new List<string>();
        void Add(string heading, bool yes) { if (yes) changed.Add(heading); }
        Add("document header", before.DocumentId != after.DocumentId || before.DocumentRevision != after.DocumentRevision
            || before.DocumentTitle != after.DocumentTitle || before.DocumentDate != after.DocumentDate || before.ApprovedById != after.ApprovedById);
        Add(MonthlyReportLayout.H61, rateBefore != rateAfter || !Same(before, after, Exec));
        Add(MonthlyReportLayout.H62, !Same(before, after, Labor) || before.TrendImageKey != after.TrendImageKey);
        Add(MonthlyReportLayout.H622, !Same(before, after, Analysis) || before.HighReturnImageKey != after.HighReturnImageKey || before.VehicleImageKey != after.VehicleImageKey);
        Add(MonthlyReportLayout.H623, Json(before.RootCause) != Json(after.RootCause));
        Add(MonthlyReportLayout.H624, Json(before.Recommendations) != Json(after.Recommendations));
        Add(MonthlyReportLayout.H63, !Same(before, after, Alerts));
        Add(MonthlyReportLayout.H64, before.Quarantine != after.Quarantine);
        Add(MonthlyReportLayout.H65, Canon(before.FirstArticle) != Canon(after.FirstArticle));
        Add(MonthlyReportLayout.H66, !Same(before, after, Ncr));
        Add(MonthlyReportLayout.H67, before.Recalls != after.Recalls);
        Add(MonthlyReportLayout.H68, before.Collabtic != after.Collabtic);
        Add(MonthlyReportLayout.H69, before.EmailImageKey != after.EmailImageKey);
        Add(MonthlyReportLayout.H610, before.FieldQuestion != after.FieldQuestion);
        Add(MonthlyReportLayout.H611, before.FitmentIntro != after.FitmentIntro || Json(before.Fitment) != Json(after.Fitment));
        Add(MonthlyReportLayout.H612, Json(before.ProductInfo) != Json(after.ProductInfo));
        Add(MonthlyReportLayout.H613, before.BlackLabelPallets != after.BlackLabelPallets);
        return changed;
    }

    static bool Same(MonthlyReportBody a, MonthlyReportBody b, string[] keys)
    {
        foreach (var k in keys)
            if (Slice(a, k) != Slice(b, k)) return false;
        return true;
    }

    static string Slice(MonthlyReportBody b, string key) => key switch
    {
        "status" => b.DepartmentStatus,
        "achievement" => b.PrimaryAchievement,
        "risks" => Json(b.CriticalRisks),
        "raw" => Canon(b.TotalClaimsRaw),
        "processed" => Canon(b.TotalClaimsProcessed),
        "dma" => Canon(b.DmaParts),
        "carter" => Canon(b.CarterParts),
        "total" => Canon(b.TotalAmountRequested),
        "parts" => Canon(b.PartsAmountRequested),
        "labor" => Canon(b.LaborAmountRequested),
        "approved" => Canon(b.ClaimsApproved),
        "denied" => Canon(b.ClaimsDenied),
        "pending" => Canon(b.ClaimsPending),
        "pendingNote" => b.ClaimsPendingNote,
        "mttf" => Canon(b.MttfDays),
        "median" => Canon(b.MedianTtfDays),
        "top" => Json(b.TopParts),
        "vehicles" => Json(b.VehicleModels),
        "alerts" => Canon(b.AlertsTotal),
        "open" => Canon(b.AlertsOpen),
        "hours" => Canon(b.HoursInvestigating),
        "avg" => Canon(b.AvgDaysToClose),
        "notes" => b.AlertNotes,
        "ncr" => Canon(b.NcrCount),
        "ncrOpen" => Canon(b.NcrOpen),
        "ncrClosed" => Canon(b.NcrClosed),
        "car" => Canon(b.CarCount),
        "pir" => Canon(b.PirCount),
        "rpn" => Canon(b.RpnCount),
        "ncrRows" => Json(b.Ncrs),
        _ => "",
    };

    static readonly string[] Exec = ["status", "achievement", "risks", "raw", "processed", "dma", "carter", "total", "parts", "labor", "approved", "denied", "pending", "pendingNote"];
    static readonly string[] Labor = ["mttf", "median", "top"];
    static readonly string[] Analysis = ["vehicles"];
    static readonly string[] Alerts = ["alerts", "open", "hours", "avg", "notes"];
    static readonly string[] Ncr = ["ncr", "ncrOpen", "ncrClosed", "car", "pir", "rpn", "ncrRows"];

    static string Json<T>(T value) => JsonSerializer.Serialize(value);
}

public sealed class TrendMonth
{
    public TrendMonth(int year, int month, string label, bool current, MonthlyReportBody? body)
    {
        Year = year;
        Month = month;
        Label = label;
        Current = current;
        TotalClaims = body?.TotalClaimsRaw;
        ProductAlerts = body?.AlertsTotal;
        TotalAmount = body?.TotalAmountRequested;
        PartsAmount = body?.PartsAmountRequested;
        LaborAmount = body?.LaborAmountRequested;
        Ratio = MonthlyReportMath.Ratio(body?.LaborAmountRequested, body?.PartsAmountRequested);
        Mttf = MonthlyReportMath.Days(body?.MttfDays);
        Median = MonthlyReportMath.Days(body?.MedianTtfDays);
        HasReport = body is not null;
    }

    public int Year { get; }
    public int Month { get; }
    public string Label { get; }
    public bool Current { get; }
    public bool HasReport { get; }
    public int? TotalClaims { get; }
    public int? ProductAlerts { get; }
    public decimal? TotalAmount { get; }
    public decimal? PartsAmount { get; }
    public decimal? LaborAmount { get; }
    public string Ratio { get; }
    public string Mttf { get; }
    public string Median { get; }
}

public abstract record ReportBlock;
public sealed record ReportTitle(string Text) : ReportBlock;
public sealed record ReportHeading(int Level, string Text) : ReportBlock;
public sealed record ReportLine(string Text) : ReportBlock;
public sealed record ReportBullets(IReadOnlyList<string> Items) : ReportBlock;
public sealed record ReportTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows) : ReportBlock;
public sealed record ReportPicture(string Slot) : ReportBlock;
public sealed record ReportStatus(string Value) : ReportBlock;

/// <summary>The repeating document-control header. The words are fields on the report, not a fixed name.</summary>
public sealed record ReportIdentity(string DocumentId, string Revision, string DocumentTitle, string DateText, string ApprovedBy)
{
    public string Banner => $"{DocumentId}: {DocumentTitle}";
    public string Line => $"Document ID: {DocumentId} | Rev: {Revision} | Title: {DocumentTitle} | Date: {DateText} | Approved By: {ApprovedBy} |";
}

public static class MonthlyReportIdentity
{
    public const string DefaultId = "TMP-ENG-001";
    public const string DefaultRevision = "C";
    public const string DefaultTitle = "Monthly Engineering Development Report";

    public static string LastDay(int year, int month) => new DateOnly(year, month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static ReportIdentity From(MonthlyReportBody body, int year, int month)
    {
        var id = string.IsNullOrWhiteSpace(body.DocumentId) ? DefaultId : body.DocumentId.Trim();
        var rev = string.IsNullOrWhiteSpace(body.DocumentRevision) ? DefaultRevision : body.DocumentRevision.Trim();
        var title = string.IsNullOrWhiteSpace(body.DocumentTitle) ? DefaultTitle : body.DocumentTitle.Trim();
        var date = DateOnly.TryParse(body.DocumentDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        return new ReportIdentity(id, rev, title, date.ToString("M/d/yyyy", CultureInfo.InvariantCulture), (body.ApprovedByName ?? "").Trim());
    }
}

/// <summary>The printed monthly report, in the same order every month.</summary>
public static class MonthlyReportLayout
{
    public const string H61 = "6.1 Executive Summary";
    public const string HFin = "Financials";
    public static string HPayout(string month) => $"Financials ({month} Payout Policy)";
    public const string H62 = "6.2 Labor Claims";
    public const string HTop = "Top Returning Parts";
    public const string H622 = "6.2.2 Analysis of High-Return Items";
    public const string HVeh = "Top Vehicle Models";
    public const string H623 = "6.2.3 Root Cause Analysis (RCA)";
    public const string H624 = "6.2.4 Recommendation";
    public const string H63 = "6.3 Product Alerts";
    public const string H64 = "6.4 Quarantine";
    public const string H65 = "6.5 First Article Verifications";
    public const string H66 = "6.6 NCRs, CARs, PIRs, & RPNs";
    public const string H67 = "6.7 - Recalls";
    public const string H68 = "6.8 - NAPA Tech Line: Collabtic";
    public const string H69 = "6.9 - OR/NAPA Email Issues";
    public const string H610 = "6.10 - Field question regarding fuel pumps";
    public const string H611 = "6.11 - Fitment Verification - O'Reilly Auto Parts";
    public const string H612 = "6.12 - Product Information";
    public const string H613 = "6.13 \u2013 NAPA black label defective fuel pump pallets";

    public static string Title(int year, int month) => $"Monthly Quality Report - {Name(month)} {year}";
    public static string Name(int month) => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

    public static List<ReportBlock> Build(int year, int month, MonthlyReportBody body, decimal flatRate, IReadOnlyList<TrendMonth> trend)
    {
        var monthName = Name(month);
        var blocks = new List<ReportBlock> { new ReportTitle(Title(year, month)), new ReportHeading(2, H61) };
        blocks.Add(new ReportStatus(Show(body.DepartmentStatus)));
        blocks.Add(new ReportLine($"Primary Achievement: {Text(body.PrimaryAchievement)}"));
        blocks.Add(new ReportLine("Critical Risk/Blocker:"));
        Bullets(blocks, body.CriticalRisks.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList());
        blocks.Add(new ReportLine(body.TotalClaimsRaw is null && body.TotalClaimsProcessed is null
            ? $"Total Claims: {MonthlyReportMath.EmptyNote}"
            : $"Total Claims: {Count(body.TotalClaimsRaw)} (Raw Export) / {Count(body.TotalClaimsProcessed)} (Tracker Processed)"));
        blocks.Add(new ReportLine("Breakdown of Fuel Pumps returned (Based on Tracker):"));
        if (body.DmaParts is null && body.CarterParts is null) Bullets(blocks, []);
        else Bullets(blocks, [$"DMA Parts: {Count(body.DmaParts)}", $"Carter / Non-DMA Pumps: {Count(body.CarterParts)}"]);
        blocks.Add(new ReportHeading(3, HFin));
        blocks.Add(new ReportLine($"Total Amount Requested: {Money(body.TotalAmountRequested)}"));
        blocks.Add(new ReportLine($"Parts Amount Requested: {Money(body.PartsAmountRequested)}"));
        blocks.Add(new ReportLine($"Labor Amount Requested: {Money(body.LaborAmountRequested)}"));
        blocks.Add(new ReportHeading(3, HPayout(monthName)));
        var potential = MonthlyReportMath.Liability(body.TotalClaimsRaw, flatRate);
        var deniedSave = MonthlyReportMath.Liability(body.ClaimsDenied, flatRate);
        var approvedLiability = MonthlyReportMath.Liability(body.ClaimsApproved, flatRate);
        blocks.Add(new ReportLine($"Total Amount Requested: {Money(body.TotalAmountRequested)}"));
        blocks.Add(new ReportLine(potential is null
            ? $"Potential Liability: {MonthlyReportMath.EmptyNote}"
            : $"Potential Liability: {Money(potential)} (Full raw claim count of {body.TotalClaimsRaw} @ {Money(flatRate)} flat rate)"));
        blocks.Add(new ReportLine($"Actual Policy Payout (Labor): We pay a {Money(flatRate)} flat rate for each defective part rather than the requested shop labor costs."));
        blocks.Add(new ReportLine(body.ClaimsDenied is null
            ? $"Claims Denied: {MonthlyReportMath.EmptyNote}"
            : $"Claims Denied: {body.ClaimsDenied} - currently saving approximately {Money(deniedSave)} in labor payouts for {monthName} ({body.ClaimsDenied} denied claims x {Money(flatRate)})."));
        blocks.Add(new ReportLine(body.ClaimsApproved is null ? $"Approved: {MonthlyReportMath.EmptyNote}" : $"Approved: {body.ClaimsApproved} claims ({Money(approvedLiability)} liability)"));
        blocks.Add(new ReportLine(body.ClaimsDenied is null ? $"Denied: {MonthlyReportMath.EmptyNote}" : $"Denied: {body.ClaimsDenied} claims"));
        blocks.Add(new ReportLine(Pending(body)));

        blocks.Add(new ReportHeading(2, H62));
        blocks.Add(new ReportLine("Overall Warranty Metrics:"));
        Bullets(blocks, [
            $"Mean Time to Failure (MTTF): {DaysBullet(body.MttfDays)}",
            $"Median Time to Failure: {DaysBullet(body.MedianTtfDays)}",
        ]);
        var labels = trend.Select(t => t.Label).ToArray();
        blocks.Add(new ReportTable(["Metric", .. labels], [
            Row("Total Claims", trend.Select(t => Count(t.TotalClaims))),
            Row("Total Product Alerts", trend.Select(t => Count(t.ProductAlerts))),
        ]));
        blocks.Add(new ReportTable(["Financial Category", .. labels], [
            Row("Total Amount Requested", trend.Select(t => Money(t.TotalAmount))),
            Row("Parts Amount Requested", trend.Select(t => Money(t.PartsAmount))),
            Row("Labor Amount Requested", trend.Select(t => Money(t.LaborAmount))),
        ]));
        blocks.Add(new ReportTable(["Warranty Metric", .. labels], [
            Row("Labor-to-Parts Ratio", trend.Select(t => t.Ratio)),
            Row("Mean Time to Failure", trend.Select(t => t.Mttf)),
            Row("Median Time to Failure", trend.Select(t => t.Median)),
        ]));
        Picture(blocks, "trend", body.TrendImageKey, trend.Any(t => t.TotalClaims is not null || t.ProductAlerts is not null));
        blocks.Add(new ReportHeading(3, HTop));
        blocks.Add(TableOrNote(["Part Number", "Description / Application", "Total Claims"],
            body.TopParts.Take(10).Where(p => !string.IsNullOrWhiteSpace(p.PartNumber) || p.TotalClaims != 0)
                .Select(p => new[] { Show(p.PartNumber), Show(p.Description), p.TotalClaims.ToString(CultureInfo.InvariantCulture) }).ToList()));
        blocks.Add(new ReportHeading(2, H622));
        Picture(blocks, "high-return", body.HighReturnImageKey, body.TopParts.Any(p => p.TotalClaims > 0));
        blocks.Add(new ReportHeading(3, HVeh));
        Picture(blocks, "vehicles", body.VehicleImageKey, body.VehicleModels.Any(v => !string.IsNullOrWhiteSpace(v.Name) && v.Count > 0));
        blocks.Add(new ReportHeading(2, H623));
        LinesOrNote(blocks, Notes(body.RootCause));
        blocks.Add(new ReportHeading(2, H624));
        LinesOrNote(blocks, Notes(body.Recommendations));
        blocks.Add(new ReportHeading(2, H63));
        blocks.Add(new ReportLine($"Total Product Alerts: {Count(body.AlertsTotal)}"));
        blocks.Add(new ReportLine($"Open: {Count(body.AlertsOpen)}"));
        blocks.Add(new ReportLine($"Hours Spent Investigating: {(body.HoursInvestigating is null ? MonthlyReportMath.EmptyNote : body.HoursInvestigating.Value.ToString("0.00", CultureInfo.InvariantCulture))}"));
        blocks.Add(new ReportLine($"Avg time to close: {(body.AvgDaysToClose is null ? MonthlyReportMath.EmptyNote : body.AvgDaysToClose.Value.ToString("0.00", CultureInfo.InvariantCulture) + " days")}"));
        blocks.Add(new ReportLine($"Notes: {Text(body.AlertNotes)}"));
        blocks.Add(new ReportHeading(2, H64));
        Bullets(blocks, Lines(body.Quarantine));
        blocks.Add(new ReportHeading(2, H65));
        var fai = body.FirstArticle.Where(r => !string.IsNullOrWhiteSpace(r.Category) && !r.Category.Trim().Equals("Total", StringComparison.OrdinalIgnoreCase)).ToList();
        var faiHeaders = new[] { "Product Category", "Total Completed", "Passed", "Failed", "Passed w/ Deviation" };
        if (fai.Count == 0) blocks.Add(TableOrNote(faiHeaders, []));
        else
        {
            var total = MonthlyReportMath.FaiTotal(fai);
            blocks.Add(new ReportTable(faiHeaders,
                [.. fai.Select(r => new[] { Show(r.Category), N(r.TotalCompleted), N(r.Passed), N(r.Failed), N(r.PassedWithDeviation) }),
                    new[] { "Total", N(total.TotalCompleted), N(total.Passed), N(total.Failed), N(total.PassedWithDeviation) }]));
        }
        blocks.Add(new ReportHeading(2, H66));
        Bullets(blocks, [
            $"Total {monthName} NCRs Logged: {Ncr(body.NcrCount, body.NcrOpen, body.NcrClosed)}",
            $"Total {monthName} CARs Logged: {Logged(body.CarCount)}",
            $"Total {monthName} RPNs Logged: {Logged(body.RpnCount)}",
        ]);
        blocks.Add(TableOrNote(["NCR Number", "Part Number", "Description of Defect", "Disposition", "Status"],
            body.Ncrs.Where(n => !string.IsNullOrWhiteSpace(n.Number) || !string.IsNullOrWhiteSpace(n.Defect))
                .Select(n => new[] { Show(n.Number), Show(n.PartNumber), Show(n.Defect), Show(n.Disposition), Show(n.Status) }).ToList()));
        blocks.Add(new ReportHeading(2, H67));
        Bullets(blocks, Lines(body.Recalls));
        blocks.Add(new ReportHeading(2, H68));
        Bullets(blocks, Lines(body.Collabtic));
        blocks.Add(new ReportHeading(2, H69));
        Picture(blocks, "email-issues", body.EmailImageKey, false);
        blocks.Add(new ReportHeading(2, H610));
        Bullets(blocks, Lines(body.FieldQuestion));
        blocks.Add(new ReportHeading(2, H611));
        var fitment = new List<string>();
        if (!string.IsNullOrWhiteSpace(body.FitmentIntro)) fitment.Add(body.FitmentIntro.Trim());
        fitment.AddRange(Pairs(body.Fitment));
        Bullets(blocks, fitment);
        blocks.Add(new ReportHeading(2, H612));
        Bullets(blocks, Pairs(body.ProductInfo));
        blocks.Add(new ReportHeading(2, H613));
        Bullets(blocks, Lines(body.BlackLabelPallets));
        return blocks;
    }

    static void Bullets(List<ReportBlock> blocks, IReadOnlyList<string> items) =>
        blocks.Add(new ReportBullets(items.Count == 0 ? [MonthlyReportMath.EmptyNote] : items));

    static void LinesOrNote(List<ReportBlock> blocks, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) blocks.Add(new ReportLine(MonthlyReportMath.EmptyNote));
        else foreach (var line in lines) blocks.Add(new ReportLine(line));
    }

    static void Picture(List<ReportBlock> blocks, string slot, string? key, bool hasData)
    {
        if (hasData || !string.IsNullOrWhiteSpace(key)) blocks.Add(new ReportPicture(slot));
        else blocks.Add(new ReportLine(MonthlyReportMath.EmptyNote));
    }

    static ReportTable TableOrNote(IReadOnlyList<string> headers, List<string[]> rows)
    {
        if (rows.Count == 0)
            rows.Add([MonthlyReportMath.EmptyNote, .. Enumerable.Repeat("", Math.Max(0, headers.Count - 1))]);
        return new ReportTable(headers, rows);
    }

    static string Pending(MonthlyReportBody body)
    {
        if (body.ClaimsPending is null) return $"Pending/Unentered: {MonthlyReportMath.EmptyNote}";
        var note = (body.ClaimsPendingNote ?? "").Trim();
        return note.Length == 0 ? $"Pending/Unentered: {body.ClaimsPending} claims" : $"Pending/Unentered: {body.ClaimsPending} claims ({note})";
    }

    static string[] Row(string name, IEnumerable<string> cells) => [name, .. cells];
    static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    static string Count(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? MonthlyReportMath.EmptyNote;
    static string Logged(int? n) => n is null ? MonthlyReportMath.EmptyNote : $"{n}.";
    static string DaysBullet(decimal? days) => days is null ? MonthlyReportMath.EmptyNote : days.Value.ToString("0.0", CultureInfo.InvariantCulture) + " days.";
    static string Show(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    static string Text(string? s) => string.IsNullOrWhiteSpace(s) ? MonthlyReportMath.EmptyNote : s.Trim();
    static string Money(decimal? v) => v is null ? MonthlyReportMath.EmptyNote : v.Value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    static string Ncr(int? total, int? open, int? closed) => total is null ? MonthlyReportMath.EmptyNote : $"{total} ({Count(open)} Open, {Count(closed)} Closed).";
    static List<string> Lines(string? text) => string.IsNullOrWhiteSpace(text) ? [] : text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    static List<string> Notes(IEnumerable<LabeledNote> notes) => notes.Where(n => !string.IsNullOrWhiteSpace(n.Label) || !string.IsNullOrWhiteSpace(n.Text))
        .Select(n => string.IsNullOrWhiteSpace(n.Label) ? n.Text.Trim() : $"{n.Label.Trim()}: {n.Text.Trim()}").ToList();
    static List<string> Pairs(IEnumerable<ProblemResponse> pairs)
    {
        var list = new List<string>();
        foreach (var p in pairs)
        {
            if (string.IsNullOrWhiteSpace(p.Problem) && string.IsNullOrWhiteSpace(p.Response)) continue;
            list.Add($"Problem: {p.Problem.Trim()}");
            list.Add($"Response: {p.Response.Trim()}");
        }
        return list;
    }
}
