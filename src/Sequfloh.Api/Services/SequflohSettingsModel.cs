using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sequfloh.Infrastructure.Modules.Execution;

// Typed SEQUFLOH settings areas. Dropdown values (types, statuses, reasons, results, categories,
// qualification labels) are administered as reference lists; the settings name the list that supplies each dropdown.

public sealed class GeneralSettings
{
    public bool ModuleEnabled { get; set; } = true;
    public string DisplayName { get; set; } = "SEQUFLOH";
    public string Description { get; set; } = "Integrated execution, tracking, validation, inspection, repair, production, and controlled work instruction management.";
    public string DefaultLandingPage { get; set; } = "/sequfloh";
    public int RecordsPerPage { get; set; } = 25;
    public Guid? DefaultSiteId { get; set; }
    public Guid? DefaultDepartmentId { get; set; }
    public int RecentItemsLimit { get; set; } = 10;
    public bool FavoritesEnabled { get; set; } = true;
}

public sealed class NumberingSettings
{
    /// <summary>"Letters" (A, B, C) or "TwoDigits" (00, 01, 02).</summary>
    public string RevisionFormat { get; set; } = "Letters";
}

public sealed class WorkInstructionSettings
{
    public bool EffectiveDateRequired { get; set; } = true;
    public bool RevisionReasonRequired { get; set; } = true;
    public bool TrainingAcknowledgementRequired { get; set; }
    public int ObsoleteRetentionYears { get; set; } = 10;
    public string PrintFooter { get; set; } = "Controlled copy when viewed in SEQUFLOH. Printed copies are uncontrolled unless stamped.";
}

public sealed class TrackingSettings
{
    public decimal VarianceWarningPercent { get; set; } = 10;
    public decimal VarianceCriticalPercent { get; set; } = 25;
    public int DefaultMinimumMinutes { get; set; } = 15;
    public int DefaultMaximumMinutes { get; set; } = 60;
    /// <summary>A task counts as overdue this many hours after its due date.</summary>
    public int OverdueThresholdHours { get; set; }
    public int ApproachingDueHours { get; set; } = 24;
    public bool VarianceReasonRequired { get; set; } = true;
    /// <summary>Type codes used in automatic work order numbers. Blank means no automatic number for that type.</summary>
    public string? ValidationNumberCode { get; set; } = "VAL";
    public string? RepairNumberCode { get; set; }
    public string? InspectionNumberCode { get; set; }
    public string? WarrantyNumberCode { get; set; }
    public string? SaltSprayNumberCode { get; set; }
    public string? DevelopmentNumberCode { get; set; } = "DEV";
    public string? ReboxNumberCode { get; set; } = "RBX";
    public string? TestNumberCode { get; set; } = "TST";
    public string? WeightNumberCode { get; set; } = "WGH";
    /// <summary>Code used in automatic inspection request numbers, "{code}-{family}{yy}-001". Blank means the number is typed.</summary>
    public string? InspectionRequestNumberCode { get; set; } = "INSP";
}

public sealed class ValidationSettings
{
    public bool EvidenceRequired { get; set; } = true;
}

public sealed class FaiSettings
{
    public bool EvidenceRequiredForFailures { get; set; } = true;
}

public sealed class RepairSettings
{
    /// <summary>No longer used; kept so stored settings still read.</summary>
    public bool RepairTestRequired { get; set; }
}

public sealed class DisplaySettings
{
    public bool CompactTables { get; set; }
    /// <summary>Fill out a copy opens the new form in its own browser window.</summary>
    public bool OpenFormsInNewWindow { get; set; }
    public List<string> DashboardCards { get; set; } = ["awaitingDispatch", "active", "paused", "overdue", "validation", "fai", "repair", "production", "failed", "released", "revised", "assigned"];
    public string DefaultTrackingFilter { get; set; } = "open";
    public string DefaultFolderView { get; set; } = "current";
    /// <summary>Status to chip colour; values use the shared status palette (default, primary, success, warning, error, info).</summary>
    public Dictionary<string, string> StatusColors { get; set; } = new()
    {
        ["Draft"] = "default", ["Released"] = "success", ["Superseded"] = "default", ["Obsolete"] = "default", ["InProgress"] = "primary", ["Paused"] = "warning", ["OnHold"] = "warning",
        ["Completed"] = "success", ["Failed"] = "error", ["Canceled"] = "default"
    };
}

public sealed class ExportSettings
{
    public string FilenamePattern { get; set; } = "{report}-{date}";
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm";
    public string OrganizationHeading { get; set; } = "";
    public Dictionary<string, List<string>> IncludedColumns { get; set; } = new();
    public Dictionary<string, string> ReportTitles { get; set; } = new()
    {
        ["work-instructions"] = "Work Instruction Status", ["validation"] = "Validation / FAI Status", ["fai"] = "Validation / FAI Status", ["repair"] = "Repair / Rework Status", ["production"] = "Production Tracking",
        ["parts"] = "Part Master", ["parts-lists"] = "Parts Lists", ["task-log"] = "Task Log", ["time-variance"] = "Time Variance", ["audit"] = "SEQUFLOH Audit Trail",
        ["user-activity"] = "User Activity", ["task-performance"] = "Task Performance"
    };
}

public sealed class NotificationSettings
{
    public bool AssignedWork { get; set; } = true;
    public bool OverdueWork { get; set; } = true;
    public bool FailedRecords { get; set; } = true;
    public bool ReleasedInstructions { get; set; } = true;
    public bool SupersededInstructions { get; set; } = true;
    public bool ApproachingDueDates { get; set; } = true;
}

public sealed class RoleSuggestion
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Permissions { get; set; } = [];
}

public sealed class SecuritySettings
{
    public bool RequireReasonForOverride { get; set; } = true;
    public bool ExportsRequirePermission { get; set; } = true;
    /// <summary>Suggested SEQUFLOH permission sets an administrator can apply to an existing role (suggestions, not roles).</summary>
    public List<RoleSuggestion> RoleSuggestions { get; set; } = [];
}

public static class SequflohJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static T Read<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new T();
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? new T(); } catch (JsonException) { return new T(); }
    }
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
