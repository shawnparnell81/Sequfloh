using System.Text;
using Sequfloh.Domain.Execution;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>One designer field. Kind: text, multiline, date, number, bool, select (controlled dropdown <see cref="List"/>).</summary>
public sealed record DesignerField(string Key, string Label, string Kind, string? List = null);
public sealed record DesignerSection(string Key, string Title, IReadOnlyList<DesignerField> Fields);
public sealed record DesignerSchema(WorkInstructionType Type, string Title, string NumberingType, IReadOnlyList<DesignerSection> Sections, string StepsTitle, IReadOnlyList<DesignerField> StepColumns,
    bool HasCharacteristics, IReadOnlyList<DesignerField> CharacteristicColumns, bool HasPartsRows, IReadOnlyList<DesignerField> PartsRowColumns);

/// <summary>
/// Designer definitions for the six instruction types. The web designers render from this schema and the release rules use it,
/// so the form and the server checks cannot drift apart. Field specs are "Label[:kind[:list]]"; the key is the camel-cased label.
/// </summary>
public static class SequflohDesigner
{
    static DesignerField F(string spec)
    {
        var p = spec.Split(':');
        return new DesignerField(Key(p[0]), p[0], p.Length > 1 ? p[1] switch { "m" => "multiline", "d" => "date", "n" => "number", "b" => "bool", "s" => "select", _ => "text" } : "text", p.Length > 2 ? p[2] : null);
    }
    public static string Key(string label)
    {
        var sb = new StringBuilder(); var up = false;
        foreach (var c in label)
        {
            if (!char.IsLetterOrDigit(c)) { up = sb.Length > 0; continue; }
            sb.Append(sb.Length == 0 ? char.ToLowerInvariant(c) : up ? char.ToUpperInvariant(c) : c); up = false;
        }
        return sb.ToString();
    }
    static DesignerSection S(string key, string title, params string[] fields) => new(key, title, fields.Select(F).ToList());
    static List<DesignerField> Cols(params string[] fields) => fields.Select(F).ToList();

    // ---------------------------------------------------------------- common content (section 17)
    static readonly DesignerSection[] Common =
    [
        S("purpose", "Purpose and scope", "Purpose:m", "Scope:m", "Intended use:m", "Exclusions:m", "Process boundaries:m"),
        S("responsibility", "Responsibility", "Responsible department", "Required role", "Required qualification:s:qualification-labels", "Responsible personnel", "Supervisor responsibility:m", "Quality responsibility:m", "Engineering responsibility:m"),
        S("safety", "Safety", "Safety warnings:m", "Hazards:m", "PPE", "Lockout or isolation requirements:m", "Handling precautions:m", "Environmental precautions:m", "Critical safety notes:m"),
        S("tools", "Tools and equipment", "Required tools:m", "Tool identification", "Equipment", "Software or test equipment", "Calibration requirement:b", "Calibration verification", "Fixtures", "Gauges", "Consumables"),
        S("materials", "Materials and parts", "Required materials:m", "Required components:m", "Replacement parts:m", "Quantities", "Units:s:units-of-measure", "Approved alternates", "Lot-traceability requirement:b"),
        S("preconditions", "Preconditions", "Prerequisite records", "Receiving condition", "Setup requirements:m", "Environmental conditions", "Initial inspection:m", "Required documentation"),
        S("acceptance", "Acceptance", "Acceptance criteria:m", "Rejection criteria:m", "Pass or fail rules:m", "Required inspection", "Required validation", "Required test", "Escalation instructions:m", "Nonconformance instructions:m"),
        S("records", "Records", "Records generated:m", "Retention requirement", "Result fields", "Evidence references", "Traceability requirements"),
    ];
    static readonly List<DesignerField> ProcedureSteps = Cols("Title", "Detailed instruction:m", "Specification", "Target:n", "Lower limit:n", "Upper limit:n", "Unit", "Tool", "Safety warning",
        "Quality checkpoint", "Expected result", "Actual result required:b", "Evidence requirement", "Visual aid reference", "Notes");

    // ---------------------------------------------------------------- validation (section 18)
    static readonly DesignerSection[] Validation =
    [
        S("validationIdentity", "Validation identity", "Validation type:s:validation-types", "Related project", "Related change", "Related assembly", "Requested by", "Validation owner", "Validation location", "Planned date:d", "Target completion date:d"),
        S("objective", "Objective", "Validation objective:m", "Requirement being tested:m", "Intended conclusion:m", "Scope:m", "Assumptions:m", "Exclusions:m", "Known risks:m"),
        S("references", "References", "Drawing reference", "Drawing revision", "Specification reference", "Engineering document reference", "Related work instruction", "Test standard", "Protocol reference", "Change record reference"),
        S("samples", "Samples", "Sample quantity:n", "Selection method", "Sample source", "Sample condition", "Lot-number requirement:b", "Serial-number requirement:b", "Control-sample requirement:b", "Comparison-sample requirement:b"),
        S("equipment", "Equipment", "Equipment name", "Equipment ID", "Calibration status", "Calibration due date:d", "Fixture", "Software or firmware version", "Environmental-monitoring equipment"),
        S("conditions", "Conditions", "Temperature", "Humidity", "Voltage", "Pressure", "Load", "Speed", "Cycle count", "Duration", "Conditioning time", "Other controlled conditions:m"),
        S("validationAcceptance", "Validation acceptance", "Overall acceptance criteria:m", "Individual characteristic rules:m", "Allowed failures", "Retest rules:m", "Deviation rules:m", "Approval conditions:m", "Rejection conditions:m"),
        S("results", "Results", "Result status:s:validation-results", "Actual result:m", "Conditional acceptance:b", "Deviation number", "Nonconformance reference", "Evidence", "Technician comments:m", "Quality comments:m", "Engineering comments:m", "Conclusion:m", "Recommended action:m"),
        S("validationApproval", "Validation outcome", "Performed by", "Dates", "Comments:m", "Final disposition"),
    ];
    static readonly List<DesignerField> ValidationSteps = Cols("Title", "Detailed action:m", "Required setup", "Input condition", "Characteristic evaluated", "Specification", "Target:n", "Lower limit:n", "Upper limit:n",
        "Unit", "Sample size:n", "Equipment", "Method", "Evidence required", "Expected result", "Actual result required:b", "Pass or fail required:b", "Deviation instructions", "Responsible role", "Required signoff");

    // ---------------------------------------------------------------- FAI (section 19)
    static readonly DesignerSection[] Fai =
    [
        S("faiIdentity", "FAI identity", "FAI type:s:fai-types", "Reason for FAI:m", "Drawing number", "Drawing revision", "Purchase order", "Manufacturing order", "Supplier", "Lot number", "Serial number",
            "Sample quantity:n", "Inspection location", "Inspector", "Required completion date:d"),
        S("faiVerification", "Verifications", "Material verification:m", "Special-process verification:m", "Certificate review:m", "Drawing balloon reference", "Dimensional inspection:m", "Visual inspection:m", "Functional inspection:m"),
        S("faiDisposition", "Disposition", "Final disposition:s:inspection-results", "Disposition comments:m"),
    ];
    static readonly List<DesignerField> Characteristic = Cols("Characteristic number", "Drawing zone", "Requirement", "Specification", "Target:n", "Lower limit:n", "Upper limit:n", "Unit", "Inspection method", "Gauge or equipment",
        "Equipment ID", "Sample quantity:n", "Actual result", "Result:s:characteristic-results", "Evidence reference", "Notes", "Nonconformance reference");

    // ---------------------------------------------------------------- repair test (section 20)
    static readonly DesignerSection[] RepairTest =
    [
        S("repairTest", "Repair test", "Applicable assembly", "Repair category:s:repair-categories", "Detected condition:m", "Test purpose:m", "Pre-test inspection:m", "Test equipment", "Equipment ID", "Calibration requirement:b",
            "Fixture", "Setup conditions:m", "Safety precautions:m", "Test parameters:m", "Expected results:m", "Pass criteria:m", "Fail criteria:m", "Retest rules:m", "Escalation instructions:m", "Evidence requirements:m"),
    ];
    static readonly List<DesignerField> TestSteps = Cols("Title", "Detailed action:m", "Equipment", "Parameter", "Target:n", "Minimum:n", "Maximum:n", "Unit", "Expected result", "Actual result required:b",
        "Pass or fail required:b", "Evidence requirement", "Notes");

    // ---------------------------------------------------------------- repair work instruction (section 21)
    static readonly DesignerSection[] RepairWork =
    [
        S("repairIdentity", "Repair document identity", "Repair category:s:repair-categories", "Repair type:s:repair-types"),
        S("partIdentification", "Part identification", "Serial-number requirement:b", "Lot-number requirement:b", "Customer-return reference", "Repair-order reference"),
        S("repairCondition", "Repair condition", "Reported problem:m", "Observed condition:m", "Failure description:m", "Defect location", "Defect category", "Initial inspection:m", "Visual-evidence references"),
        S("repairDescription", "Repair description", "Detailed description:m", "Repair objective:m", "Expected restored condition:m", "Limitations:m", "Prohibited actions:m"),
        S("safety", "Safety", "Hazards:m", "PPE", "Stored-energy precautions:m", "Electrical precautions:m", "Mechanical precautions:m", "Chemical precautions:m", "Handling requirements:m", "Contamination controls:m"),
        S("tools", "Tools and equipment", "Tools:m", "Fixtures", "Gauges", "Test equipment", "Equipment IDs", "Calibration requirements", "Consumables", "Approved chemicals", "Torque tools"),
        S("postRepair", "Post-repair", "Visual inspection:b", "Dimensional inspection:b", "Electrical test:b", "Mechanical test:b", "Functional test:b", "Leak test:b", "Load test:b", "Cycle test:b", "Final cleaning:b",
            "Labeling:b", "Packaging:b"),
        S("acceptance", "Acceptance", "Restored condition:m", "Inspection requirements:m", "Test requirements:m", "Allowable conditions:m", "Rejection conditions:m", "Rework rules:m", "Escalation requirements:m", "Nonconformance instructions:m"),
        S("approval", "Completion", "Performed by", "Inspected by", "Dates", "Comments:m"),
    ];
    static readonly List<DesignerField> RepairSteps = Cols("Operation step", "Title", "Detailed repair action:m", "Tools required", "Parts consumed", "Quantity consumed:n", "Specification", "Torque value", "Minimum:n", "Maximum:n", "Unit",
        "Visual checkpoint", "Quality checkpoint", "Expected result", "Evidence requirement", "Warning", "Technician result required:b", "Pass or fail required:b", "Notes");
    static readonly List<DesignerField> PartsRowColumns = Cols("Replacement part number", "Description", "Quantity:n", "Unit", "Required:b", "Approved alternate", "Lot number required:b", "Serial number required:b",
        "Issued quantity:n", "Used quantity:n", "Returned quantity:n");

    // ---------------------------------------------------------------- inspection
    static readonly DesignerSection[] Inspection =
    [
        S("inspectionIdentity", "Inspection identity", "Inspection reason:m", "Drawing number", "Drawing revision", "Lot number", "Sample quantity:n", "Sampling plan", "Inspection location", "Inspector"),
        S("inspectionDisposition", "Disposition", "Final disposition:s:inspection-results", "Quantity inspected:n", "Quantity good:n", "Quantity nonconforming:n", "Disposition comments:m"),
    ];

    static IReadOnlyList<DesignerSection> Merge(IEnumerable<DesignerSection> specific, IEnumerable<DesignerSection>? common = null)
    {
        var s = specific.ToList();
        var c = (common ?? Common).Where(x => s.All(y => y.Key != x.Key));
        return [.. s, .. c];
    }

    public static readonly IReadOnlyDictionary<WorkInstructionType, DesignerSchema> Schemas = new Dictionary<WorkInstructionType, DesignerSchema>
    {
        [WorkInstructionType.Validation] = new(WorkInstructionType.Validation, "Validation / FAI", "SqfValidationInstruction", Merge(Validation), "Validation steps", ValidationSteps, false, [], false, []),
        [WorkInstructionType.ValidationFai] = new(WorkInstructionType.ValidationFai, "Validation / FAI", "SqfWorkInstruction", Merge([.. Validation, .. Fai]), "Validation steps", ValidationSteps, true, Characteristic, false, []),
        [WorkInstructionType.Fai] = new(WorkInstructionType.Fai, "Validation / FAI", "SqfFaiInstruction", Merge(Fai), "Procedure steps", ProcedureSteps, true, Characteristic, false, []),
        [WorkInstructionType.RepairTest] = new(WorkInstructionType.RepairTest, "Repair / Rework Test", "SqfRepairTestInstruction", Merge(RepairTest), "Test steps", TestSteps, false, [], false, []),
        [WorkInstructionType.RepairWork] = new(WorkInstructionType.RepairWork, "Repair / Rework Work Instruction", "SqfRepairWorkInstruction", Merge(RepairWork), "Repair steps", RepairSteps, false, [], true, PartsRowColumns),
        [WorkInstructionType.Inspection] = new(WorkInstructionType.Inspection, "Inspection", "SqfInspectionInstruction", Merge(Inspection), "Inspection steps", ProcedureSteps, false, [], false, []),
        // imported files that apply to every part of a product family: the file is the content, so there are no sections or steps
        [WorkInstructionType.Procedure] = new(WorkInstructionType.Procedure, "Procedure Instructions", "SqfInspectionInstruction", [], "Steps", ProcedureSteps, false, [], false, []),
    };

    /// <summary>Procedure Instructions are imported files only: no blocks, steps or fields are edited in the app, and they never run as work orders.</summary>
    public static bool IsFileOnly(WorkInstructionType t) => t == WorkInstructionType.Procedure;

    /// <summary>The validation folder holds Validation, Validation / FAI and FAI; the repair folder holds Repair Test and Repair Work Instructions.</summary>
    public static bool IsValidationFamily(WorkInstructionType t) => t is WorkInstructionType.Validation or WorkInstructionType.ValidationFai or WorkInstructionType.Fai;
}
