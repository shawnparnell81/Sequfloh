using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.MasterData;

/// <summary>Part master records. Each part has an owning site for access scope.</summary>
public enum MasterStatus { Active = 0, Inactive = 1, Obsolete = 2 }

public enum PartType { Component = 0, Assembly = 1, Material = 2, Subassembly = 3, Consumable = 4, Packaging = 5, Other = 9 }

public class Part : Entity, IAudited, ISiteScoped, ISoftDeletable
{
    /// <summary>Informational status from the admin-editable status list, for example "Quarantine hold (NetSuite)". Never blocks anything; official holds are in NetSuite.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string? StatusTag { get; set; }
    /// <summary>Optional free-text note that goes with the status.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(300)]
    public string? StatusNote { get; set; }
    /// <summary>Free notes about the part, shown at the top of the part and where it is picked.</summary>
    public string? Notes { get; set; }
    public string PartNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public PartType PartType { get; set; }
    public string Revision { get; set; } = "A";
    public string UnitOfMeasure { get; set; } = "Each";
    public Guid? ProductId { get; set; }
    public string? Material { get; set; }
    public bool HasSpecialCharacteristics { get; set; }
    public string? ProductFamily { get; set; }
    public string? AssemblyType { get; set; }
    /// <summary>Part class, chosen from the product families list.</summary>
    public string? PartClass { get; set; }
    /// <summary>The part is an assembly (Yes / No).</summary>
    public bool IsAssembly { get; set; }
    /// <summary>Lot controlled: Yes, No or Not applicable. IsLotControlled is true only for Yes.</summary>
    public string? LotControl { get; set; }
    /// <summary>The part itself can be repaired (it can be the subject of a repair work order).</summary>
    public bool IsRepairable { get; set; }
    /// <summary>A component or replacement part ordered to carry out repairs (screws, nuts, mounts). Set on its own, never from Repairable.</summary>
    public bool IsRepairPart { get; set; }
    public bool IsSerialized { get; set; }
    public bool IsLotControlled { get; set; }
    public string? DrawingNumber { get; set; }
    public string? Specification { get; set; }
    /// <summary>Part type from the admin-managed part types list (for example Fuel Pump or Strut). Empty on parts added before part types existed.</summary>
    public Guid? TypeId { get; set; }
    /// <summary>Cross-reference numbers and other attributes as JSON, keyed by the definitions in PartAttributes.</summary>
    public string? Attributes { get; set; }
    /// <summary>The attribute values joined into one line for searching.</summary>
    public string? AttributeText { get; set; }
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public MasterStatus Status { get; set; }
    public string AuditLabel => $"Part {PartNumber} rev {Revision}";
}

/// <summary>Immutable history of part revisions: a revision change adds a row, it never overwrites one.</summary>
public class PartRevision : Entity, IAudited
{
    public Guid PartId { get; set; }
    public string Revision { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateOnly EffectiveOn { get; set; }
    public string AuditLabel => $"Part revision {Revision}";
}
