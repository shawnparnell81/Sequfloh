using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

/// <summary>
/// SEQUFLOH module settings (one row for the organization). Each settings area is stored as a JSON document (jsonb) and
/// read through typed settings in the SEQUFLOH settings service; every change is written to the shared audit trail.
/// </summary>
public class SequflohSettings : Entity, IAudited
{
    public string General { get; set; } = "{}";
    public string Numbering { get; set; } = "{}";
    public string WorkInstructions { get; set; } = "{}";
    public string Tracking { get; set; } = "{}";
    public string Validation { get; set; } = "{}";
    public string Fai { get; set; } = "{}";
    public string Repair { get; set; } = "{}";
    public string Display { get; set; } = "{}";
    public string Export { get; set; } = "{}";
    public string Notifications { get; set; } = "{}";
    public string Security { get; set; } = "{}";
    /// <summary>The blank form an administrator confirmed for Salt Spray Test work orders (a test, not a product family).</summary>
    public Guid? SaltSprayFormId { get; set; }
    public string AuditLabel => "SEQUFLOH settings";
}

/// <summary>
/// Administrable dropdown value of a SEQUFLOH reference list (work instruction types, repair descriptions, hold reasons, ...).
/// Seeded values stay editable; values are deactivated, never deleted, so records that used them keep their meaning.
/// </summary>
public class SequflohReferenceValue : Entity, IAudited
{
    public string ListKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    /// <summary>Created by the seed; a reset restores seeded labels only after an explicit confirmed reset action.</summary>
    public bool IsSeeded { get; set; }
    /// <summary>Product families only: fields that start as Not applicable on new and imported parts of the family, comma-separated keys (see PartFields).</summary>
    public string? HiddenFields { get; set; }
    /// <summary>Product families only: the blank form an administrator confirmed as this family's validation form. Null until confirmed.</summary>
    public Guid? ValidationFormId { get; set; }
    public string AuditLabel => $"SEQUFLOH {ListKey} value {Code}";
}

/// <summary>Recently opened and pinned SEQUFLOH records and pages of a user (no shared recent-items service exists).</summary>
public class SequflohUserItem : Entity
{
    public Guid UserId { get; set; }
    /// <summary>"recent" or "favorite".</summary>
    public string Kind { get; set; } = "";
    public string ItemType { get; set; } = "";
    public Guid? RecordId { get; set; }
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTimeOffset LastOpenedAt { get; set; }
}
