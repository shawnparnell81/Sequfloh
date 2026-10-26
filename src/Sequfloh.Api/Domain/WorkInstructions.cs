using System.Text.Json.Serialization;
using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkInstructionType { Validation = 0, ValidationFai = 1, Fai = 2, RepairTest = 3, RepairWork = 4, Inspection = 5, Procedure = 6 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkInstructionStatus { Draft = 0, Released = 6, Superseded = 7, Obsolete = 8 }

/// <summary>
/// Controlled SEQUFLOH work instruction (one row per revision). Document control and applicability are columns; the designer sections, steps,
/// characteristics and parts rows are jsonb copies owned by this revision (copied library data never links back to the template).
/// </summary>
public class SequflohWorkInstruction : Entity, IAudited, ISiteScoped, ISoftDeletable
{
    /// <summary>Informational status from the admin-editable status list, for example "Quarantine hold (NetSuite)". Never blocks anything; official holds are in NetSuite.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string? StatusTag { get; set; }
    /// <summary>Optional free-text note that goes with the status.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(300)]
    public string? StatusNote { get; set; }
    public Guid FamilyId { get; set; }
    /// <summary>True when imported in Engineering › Validation / FAI › Validation instructions. Those are listed only there and in Released validation instructions.</summary>
    public bool ValidationFolder { get; set; }
    /// <summary>The Engineering, Development product line it was imported or created in (see DevelopmentLines). Those are listed only in that line and used on Development work orders.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(40)]
    public string? DevelopmentLine { get; set; }
    public Guid? PreviousRevisionId { get; set; }
    public string Number { get; set; } = "";
    public WorkInstructionType Type { get; set; }
    public string Title { get; set; } = "";
    public string Revision { get; set; } = "A";
    public WorkInstructionStatus Status { get; set; }
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? AuthorId { get; set; }
    public Guid? PartId { get; set; }
    public string? PartNumber { get; set; }
    public string? PartDescription { get; set; }
    public string? ProductFamily { get; set; }
    public string? Assembly { get; set; }
    public string? Model { get; set; }
    public string? Variant { get; set; }
    public string? Process { get; set; }
    public string? Operation { get; set; }
    public string? Workstation { get; set; }
    public string? Location { get; set; }
    /// <summary>Validation type, FAI type, repair category, repair type, repair description (controlled dropdowns), kept as columns for folders and filters.</summary>
    public string? ValidationType { get; set; }
    public string? FaiType { get; set; }
    public string? RepairCategory { get; set; }
    public string? RepairType { get; set; }
    public string? RepairDescription { get; set; }
    public Guid? SourcePartsListId { get; set; }
    public string? ResultStatus { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset? ObsoleteAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedById { get; set; }
    public string? RevisionReason { get; set; }
    // Engineering development record
    public Guid? AssignedEngineerId { get; set; }
    public DateOnly? TargetCompletionDate { get; set; }
    public string? Project { get; set; }
    public string? EngineeringNotes { get; set; }
    public string? DevelopmentStatus { get; set; }
    /// <summary>Section key -> field key -> value.</summary>
    public string Content { get; set; } = "{}";
    /// <summary>Ordered steps (validation, test, repair or procedure steps) with explicit sequence numbers.</summary>
    public string Steps { get; set; } = "[]";
    /// <summary>FAI characteristics.</summary>
    public string Characteristics { get; set; } = "[]";
    /// <summary>Parts rows copied from a parts list or picked individually.</summary>
    public string PartsRows { get; set; } = "[]";
    /// <summary>
    /// Free layout the author arranges: ordered blocks of text, images or visual aids, tables, numbered steps with operation numbers,
    /// cautions and warnings, and checks (see <c>SequflohBlocks</c>).
    /// </summary>
    public string Blocks { get; set; } = "[]";
    /// <summary>The reusable template the blocks were started from, if any (copied, never linked back).</summary>
    public Guid? TemplateId { get; set; }
    public string AuditLabel => $"work instruction {Number} rev {Revision}";
}

/// <summary>A reusable arrangement of work instruction blocks that authors start new instructions from or add to existing ones.</summary>
public class SequflohInstructionTemplate : Entity, IAudited
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Instruction type the template is meant for; null suits every type.</summary>
    public WorkInstructionType? Type { get; set; }
    public string Blocks { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public Guid? OwnerId { get; set; }
    public string AuditLabel => $"work instruction template {Name}";
}

/// <summary>Lifecycle transition (append-only): previous and new status, action, user, time, comment, revision, record.</summary>
public class SequflohWorkInstructionTransition : Entity, IAppendOnly
{
    public Guid WorkInstructionId { get; set; }
    public string Action { get; set; } = "";
    public WorkInstructionStatus? FromStatus { get; set; }
    public WorkInstructionStatus ToStatus { get; set; }
    public Guid? UserId { get; set; }
    public string? Comment { get; set; }
    public string Revision { get; set; } = "";
}
