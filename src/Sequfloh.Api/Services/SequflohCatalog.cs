using Sequfloh.Domain.Security;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record ReferenceListDef(string Key, string Label, string Description, string[] Seed, bool NoDefault = false);
public sealed record NumberingDef(string RecordType, string Label, string Prefix);

/// <summary>Seed content for SEQUFLOH: reference lists, numbering sequences and suggested permission sets. Every value stays editable.</summary>
public static class SequflohCatalog
{
    /// <summary>The admin-managed product families list; a part stores the value Id in TypeId and a copy of the label in ProductFamily.</summary>
    public const string ProductFamilies = "product-families";

    /// <summary>Family prefixes such as INJ- that a drawing file or a part number may leave out when drawings are linked to parts.</summary>
    public const string DrawingPrefixes = "drawing-prefixes";

    public const string Module = "sequfloh";

    public static readonly ReferenceListDef[] Lists =
    [
        new("work-instruction-types", "Work instruction types", "Instruction types offered when a work instruction is created.", ["Validation", "Validation / FAI", "FAI", "Repair Test", "Repair Work Instruction", "Inspection", "Procedure Instructions"]),
        new("lifecycle-statuses", "Lifecycle statuses", "Display labels of the work instruction lifecycle statuses.", ["Draft", "Released", "Superseded", "Obsolete"]),
        new("activity-statuses", "Activity statuses", "Display labels of the tracking and task statuses.", ["Not Started", "Assigned", "In Progress", "Paused", "On Hold", "Completed", "Failed", "Canceled"]),
        new("validation-types", "Validation types", "Kinds of validation.", ["Design validation", "Process validation", "Equipment qualification", "Software validation", "Test method validation", "Packaging validation", "Material validation"]),
        new("validation-results", "Validation results", "Results of a validation.", ["Pending", "Pass", "Fail", "Conditional"]),
        new("fai-types", "FAI types", "Kinds of first article inspection.", ["Full FAI", "Partial FAI", "Delta FAI"]),
        new("inspection-results", "Inspection results", "Results of an FAI inspection.", ["Pending", "Accepted", "Rejected", "Conditionally accepted"]),
        new("characteristic-results", "Characteristic results", "Results of one FAI characteristic.", ["Pass", "Fail", "Not applicable"]),
        new("repair-types", "Repair types", "Kinds of repair.", ["Field return repair", "In-process repair", "Warranty repair", "Customer-paid repair", "Refurbishment"]),
        new("repair-categories", "Repair categories", "Repair categories.", ["Electrical", "Mechanical", "Cosmetic", "Hydraulic / pneumatic", "Software / firmware", "Structural"]),
        new("repair-descriptions", "Repair descriptions", "Controlled repair descriptions used by Repair Work Instructions.",
            ["Inspection and diagnosis", "Disassembly", "Cleaning", "Component replacement", "Adjustment", "Rework", "Electrical repair", "Mechanical repair", "Cosmetic repair", "Leak correction",
             "Connector repair", "Fastener replacement", "Functional restoration", "Final testing", "Other controlled repair"]),
        new("repair-test-results", "Repair test results", "Results of a repair test.", ["Pass", "Fail", "Retest required"]),
        new("repair-failure-reasons", "Repair failure reasons", "Why a repair or repair test failed.", ["Fault not found", "Repair ineffective", "Additional damage found", "Part not available", "Test equipment fault", "Workmanship"]),
        new("discovery-methods", "Discovery methods", "How a supplier nonconformance was found, on the work order findings and chargeback report.",
            ["AQL sampling inspection", "Warranty claim inspection", "Product alert", "Recall", "Incoming inspection", "In-process", "Customer complaint", "Other"]),
        new("inspection-reasons", "Inspection request reasons", "Why an inspection is requested.",
            ["Supplier request", "Warranty claim", "AQL sample result", "Random inspection", "Customer request", "Defect driven"]),
        new("inspection-priorities", "Inspection request priorities", "Priority levels for inspection requests, highest first. The order sets the queue order.", ["Critical", "High", "Normal", "Low"]),
        new(DrawingPrefixes, "Drawing number prefixes", "Family prefixes, such as INJ- for fuel injectors, that a drawing file name or a part number may leave out. D1004Z.pdf then links to part INJ-D1004Z, and the other way round. Used when drawings are imported and by Relink.", ["INJ-"], NoDefault: true),
        new("priorities", "Priorities", "Priority levels of tasks and tracking records.", ["Low", "Normal", "High", "Urgent"]),
        new("cancellation-reasons", "Cancellation reasons", "Why a task is canceled.", ["Order canceled", "Duplicate task", "Created in error", "Superseded by another task", "Other"]),
        new("units-of-measure", "Units of measure", "Units used in steps, characteristics and parts lists.", ["EA", "mm", "in", "N·m", "lbf·in", "V", "A", "Ω", "°C", "%RH", "kPa", "psi", "s", "min", "g", "kg"]),
        new("qualification-labels", "Qualification labels", "Qualifications required for steps and technicians.", ["Trained operator", "Certified technician", "Senior technician", "Qualified inspector", "ESD trained", "Soldering certified"]),
        new(ProductFamilies, "Product families", "The product family chosen when a part is added, for example Complete Strut Assembly (CSA) or Fuel Pump. Renaming a family renames it on every part; a deactivated family stays on existing parts but is no longer offered. In Parts import a value matches the label, the code, or either part of a label such as \"Complete Strut Assembly (CSA)\".",
            ["Complete Strut Assembly (CSA)", "Fuel Pump", "Fuel Injector", "Brake Wear Sensor", "Gas Lift", "Electric Lift", "Coil Spring", "Struts", "Air Struts"], NoDefault: true),
        new("operation-categories", "Operation categories", "Categories of reusable operation steps.", ["Inspection", "Disassembly", "Assembly", "Cleaning", "Test", "Adjustment", "Packaging", "Labeling", "Safety"]),
        new("equipment-types", "Equipment types", "Equipment types used in instructions.", ["Hand tool", "Torque tool", "Gauge", "Test equipment", "Fixture", "Software", "Environmental monitor"]),
        new("lifecycle-actions", "Lifecycle actions", "Display labels of work instruction actions.", ["Release", "Create revision", "Mark obsolete"]),
        new("document-types", "Document types", "Kinds of referenced documents (drawings, specifications, procedures).", ["Drawing", "Specification", "Procedure", "Test standard", "Protocol", "Engineering document", "Change record"]),
        new("evidence-types", "Evidence types", "Kinds of evidence references.", ["Photo", "Measurement record", "Test report", "Certificate", "Data file"]),
    ];

    public static readonly NumberingDef[] Numbering =
    [
        new("SqfWorkInstruction", "Work instructions (Validation / FAI)", "WI-"),
        new("SqfValidationInstruction", "Validation instructions", "VWI-"),
        new("SqfFaiInstruction", "FAI instructions", "FWI-"),
        new("SqfRepairTestInstruction", "Repair test instructions", "RTI-"),
        new("SqfRepairWorkInstruction", "Repair work instructions", "RWI-"),
        new("SqfInspectionInstruction", "Inspection instructions", "IWI-"),
        new("SqfValidationRecord", "Validation records", "VAL-T-"),
        new("SqfFaiRecord", "FAI records", "FAI-T-"),
        new("SqfRepairRecord", "Repair records", "RPR-"),
        new("SqfProductionTask", "Production tasks", "PT-"),
        new("SqfPartsList", "Parts lists", "PL-"),
        new("SqfOperationStep", "Reusable operation steps", "OP-"),
        new("SqfImportedFile", "Imported files", "IMP-"),
        new("SqfInspectionRequest", "Inspection requests", "IR-"),
    ];

    static string[] P(params string[] caps) => caps.Select(c => "sequfloh." + c).ToArray();

    public static List<RoleSuggestion> DefaultRoleSuggestions() =>
    [
        new() { Name = "Operator", Description = "Runs assigned production tasks at a station.", Permissions = [.. P("access", "view_dashboard", "view_live_execution", "start_task", "pause_task", "resume_task", "complete_task")] },
        new() { Name = "Technician", Description = "Performs validation, FAI and repair work.", Permissions = [.. P("access", "view_dashboard", "view_live_execution", "start_task", "pause_task", "resume_task", "complete_task", "fail_task", "create_repair_record")] },
        new() { Name = "Supervisor", Description = "Dispatches and reassigns work, creates tracking records.", Permissions = [.. P("access", "view_dashboard", "view_live_execution", "dispatch_task", "start_task", "pause_task", "resume_task", "complete_task", "fail_task", "reassign_task", "cancel_task", "create_validation_record", "create_fai_record", "create_repair_record", "create_production_task", "view_reports", "export_records", "manage_crew_time", "manage_chargebacks")] },
        new() { Name = "Quality", Description = "Releases instructions and checks results.", Permissions = [.. P("access", "view_dashboard", "view_live_execution", "release_work_instruction", "obsolete_work_instruction", "view_quality_folder", "edit_quality_folder", "view_imported_files", "import_files", "view_drawings", "view_reports", "export_records", "view_audit_trail", "create_fai_record", "create_validation_record")] },
        new() { Name = "Engineering", Description = "Develops and revises work instructions, parts lists and operation steps.", Permissions = [.. P("access", "view_dashboard", "create_work_instruction", "edit_draft_work_instruction", "revise_work_instruction", "view_engineering_folder", "edit_engineering_folder", "view_imported_files", "import_files", "view_drawings", "manage_drawings", "edit_parts", "manage_parts_lists", "manage_operation_steps", "view_reports")] },
        new() { Name = "Document control", Description = "Releases instructions and marks them obsolete.", Permissions = [.. P("access", "view_dashboard", "release_work_instruction", "obsolete_work_instruction", "view_audit_trail", "view_reports", "export_records")] },
        new() { Name = "Module administrator", Description = "Administers SEQUFLOH settings, dropdowns and module access.", Permissions = [.. Permissions.All.Where(p => p.StartsWith("sequfloh.", StringComparison.Ordinal))] },
    ];

    public static IReadOnlyList<string> AllPermissions => Permissions.All.Where(p => p.StartsWith("sequfloh.", StringComparison.Ordinal)).ToList();
}
