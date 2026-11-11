using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

/// <summary>An administrator-defined dropdown. Built-in reference lists are separate and are not stored here.</summary>
public class CustomDropdown : Entity, IAudited
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string AuditLabel => $"dropdown {Name}";
}

/// <summary>One choice on an administrator-defined dropdown.</summary>
public class CustomDropdownValue : Entity, IAudited
{
    public Guid DropdownId { get; set; }
    public string Label { get; set; } = "";
    public string Code { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
    public string AuditLabel => $"dropdown value {Label}";
}

/// <summary>Where an administrator-defined dropdown is shown, and how that screen uses it.</summary>
public class CustomDropdownPlacement
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DropdownId { get; set; }
    public string Screen { get; set; } = "";
    public bool Required { get; set; }
    public bool ShowInList { get; set; }
    public bool IncludeInCsv { get; set; }
    public bool Filterable { get; set; }
}

/// <summary>The chosen value of one custom dropdown on one record. The same table holds every record type.</summary>
public class CustomFieldValue : Entity, IAudited
{
    public Guid DropdownId { get; set; }
    public Guid? ValueId { get; set; }
    public string RecordType { get; set; } = "";
    public Guid RecordId { get; set; }
    public string AuditLabel => $"custom field on {RecordType}";
}
