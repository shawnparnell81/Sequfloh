using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Organization;

/// <summary>Single-row organization and branding configuration. Nothing in source depends on these values.</summary>
public class OrganizationSettings : Entity, IAudited
{
    public string ApplicationName { get; set; } = "SEQUFLOH";
    public string OrganizationName { get; set; } = "";
    public string TimeZone { get; set; } = "America/New_York";
    public string AuditLabel => "Organization settings";
}

public class Site : Entity, IAudited, IArchivable
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    /// <summary>IANA time zone for business dates at this site (due, overdue, effective, "today").</summary>
    public string TimeZone { get; set; } = "America/New_York";
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<Department> Departments { get; set; } = [];
    public string AuditLabel => $"Site {Code}";
}

public class Department : Entity, IAudited, IArchivable
{
    public Guid SiteId { get; set; }
    public Site? Site { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string AuditLabel => $"Department {Code}";
}
