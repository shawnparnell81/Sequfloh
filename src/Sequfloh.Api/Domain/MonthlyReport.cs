using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

/// <summary>One monthly quality report. The body is the editable report; status is Draft until it is finalized.</summary>
public class MonthlyQualityReport : Entity
{
    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>Draft or Final.</summary>
    public string Status { get; set; } = "Draft";
    /// <summary>Flat labor payout used for potential liability. The report stores the rate that was in effect for the month.</summary>
    public decimal FlatRate { get; set; } = 75m;
    /// <summary>Report fields, import origins and image keys (JSON).</summary>
    public string Body { get; set; } = "{}";
    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedById { get; set; }
    public string? FinalizedByName { get; set; }
    public DateTimeOffset? ReopenedAt { get; set; }
    public Guid? ReopenedById { get; set; }
    public string? ReopenReason { get; set; }
    public string CreatedByName { get; set; } = "";
}

/// <summary>A workbook or picture uploaded for one month. The bytes live in file storage; this row records the type, the month and the import result.</summary>
public class MonthlyReportFile : Entity
{
    public Guid ReportId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string FileType { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";
    public string UploadedByName { get; set; } = "";
    /// <summary>The first calendar day of the following month. Rows on or after this date belong to a later month.</summary>
    public DateOnly ReportingCutoff { get; set; }
    /// <summary>Warnings, skipped rows and counts from the import (JSON). Empty when the file is only stored.</summary>
    public string Result { get; set; } = "{}";
}

/// <summary>The editable contents of a monthly quality report. Every month has the same fields.</summary>
public sealed class MonthlyReportBody
{
    /// <summary>Document-control header. Copied from the previous month when a report is started that way.</summary>
    public string DocumentId { get; set; } = "TMP-ENG-001";
    public string DocumentRevision { get; set; } = "C";
    public string DocumentTitle { get; set; } = "Monthly Engineering Development Report";
    /// <summary>Calendar date shown in the header, yyyy-MM-dd. Empty uses the last day of the report month.</summary>
    public string? DocumentDate { get; set; }
    public Guid? ApprovedById { get; set; }
    public string ApprovedByName { get; set; } = "";
    public string DepartmentStatus { get; set; } = "";
    public string PrimaryAchievement { get; set; } = "";
    public List<string> CriticalRisks { get; set; } = [];
    public int? TotalClaimsRaw { get; set; }
    public int? TotalClaimsProcessed { get; set; }
    public int? DmaParts { get; set; }
    public int? CarterParts { get; set; }
    public decimal? TotalAmountRequested { get; set; }
    public decimal? PartsAmountRequested { get; set; }
    public decimal? LaborAmountRequested { get; set; }
    public int? ClaimsApproved { get; set; }
    public int? ClaimsDenied { get; set; }
    public int? ClaimsPending { get; set; }
    /// <summary>Optional words after the pending count, printed in parentheses.</summary>
    public string ClaimsPendingNote { get; set; } = "";
    public decimal? MttfDays { get; set; }
    public decimal? MedianTtfDays { get; set; }
    public List<PartClaimRow> TopParts { get; set; } = [];
    public List<NamedCount> VehicleModels { get; set; } = [];
    public List<LabeledNote> RootCause { get; set; } = [];
    public List<LabeledNote> Recommendations { get; set; } = [];
    public int? AlertsTotal { get; set; }
    public int? AlertsOpen { get; set; }
    public decimal? HoursInvestigating { get; set; }
    public decimal? AvgDaysToClose { get; set; }
    public string AlertNotes { get; set; } = "";
    public string Quarantine { get; set; } = "";
    public List<FaiRow> FirstArticle { get; set; } = [];
    public int? NcrCount { get; set; }
    public int? NcrOpen { get; set; }
    public int? NcrClosed { get; set; }
    public int? CarCount { get; set; }
    public int? PirCount { get; set; }
    public int? RpnCount { get; set; }
    public List<NcrRow> Ncrs { get; set; } = [];
    public string Recalls { get; set; } = "";
    public string Collabtic { get; set; } = "";
    public string FieldQuestion { get; set; } = "";
    /// <summary>Leading bullet in 6.11, ahead of the problem and response pairs.</summary>
    public string FitmentIntro { get; set; } = "";
    public List<ProblemResponse> Fitment { get; set; } = [];
    public List<ProblemResponse> ProductInfo { get; set; } = [];
    public string BlackLabelPallets { get; set; } = "";
    public string? TrendImageKey { get; set; }
    public string? HighReturnImageKey { get; set; }
    public string? VehicleImageKey { get; set; }
    public string? EmailImageKey { get; set; }
    /// <summary>Where a filled value came from. The person can type over it; the computed text stays here.</summary>
    public Dictionary<string, FieldOrigin> Origin { get; set; } = new();
}

public sealed class FieldOrigin
{
    public string FileType { get; set; } = "";
    public string FileName { get; set; } = "";
    public Guid FileId { get; set; }
    public string Tab { get; set; } = "";
    public string Computed { get; set; } = "";
    public bool Overridden { get; set; }
}

public sealed class PartClaimRow
{
    public string PartNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public int TotalClaims { get; set; }
}

public sealed class NamedCount
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public sealed class LabeledNote
{
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class FaiRow
{
    public string Category { get; set; } = "";
    public int TotalCompleted { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int PassedWithDeviation { get; set; }
}

public sealed class NcrRow
{
    public string Number { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string Defect { get; set; } = "";
    public string Disposition { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class ProblemResponse
{
    public string Problem { get; set; } = "";
    public string Response { get; set; } = "";
}
