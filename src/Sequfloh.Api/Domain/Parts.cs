using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum PartsListType { Standard = 0, RepairKit = 1, AssemblyBom = 2, RepairParts = 3, Custom = 4 }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum PartsListStatus { Draft = 0, Released = 1, Inactive = 2 }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum TraceabilityRequirement { None = 0, Lot = 1, Serial = 2, LotAndSerial = 3 }

/// <summary>Controlled parts list (standard list, repair kit, assembly BOM, repair parts, custom). A released list is changed only through a new revision.</summary>
public class SequflohPartsList : Entity, IAudited
{
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public Guid? ParentPartId { get; set; }
    public string Revision { get; set; } = "A";
    public PartsListStatus Status { get; set; }
    public string? Description { get; set; }
    public PartsListType ListType { get; set; }
    public Guid? ReleasedById { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public List<SequflohPartsListItem> Items { get; set; } = [];
    public string AuditLabel => $"parts list {Number} rev {Revision}";
}

public class SequflohPartsListItem : Entity, IAudited
{
    public Guid PartsListId { get; set; }
    public int ItemNumber { get; set; }
    public Guid? PartId { get; set; }
    public string PartNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "EA";
    public bool Required { get; set; } = true;
    public string? AlternatePartNumber { get; set; }
    public TraceabilityRequirement Traceability { get; set; }
    public string? Notes { get; set; }
    public string AuditLabel => $"parts list item {ItemNumber} ({PartNumber})";
}

/// <summary>Frozen copy of a parts list revision (header and items) taken when the next revision starts. Never changed.</summary>
public class SequflohPartsListRevision : Entity, IAppendOnly
{
    public Guid PartsListId { get; set; }
    public string Revision { get; set; } = "";
    public PartsListStatus StatusAtRevision { get; set; }
    public string Snapshot { get; set; } = "{}";
    public string? Reason { get; set; }
}

/// <summary>Reusable operation-step template. Copies placed in instructions are instruction-specific and never change the template.</summary>
public class SequflohOperationStep : Entity, IAudited
{
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public string DetailedAction { get; set; } = "";
    public string? DefaultTools { get; set; }
    public string? DefaultParts { get; set; }
    public string? Specification { get; set; }
    public decimal? Target { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public string? Unit { get; set; }
    public string? SafetyWarning { get; set; }
    public string? QualityCheckpoint { get; set; }
    public string? ExpectedResult { get; set; }
    public string? EvidenceRequirement { get; set; }
    public string? RequiredRole { get; set; }
    public string? RequiredQualification { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
    public string AuditLabel => $"operation step {Number}";
}
