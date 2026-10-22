using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Security;

/// <summary>A role is a named, configurable set of permissions. Roles are data, never hard-coded checks.</summary>
public class Role : Entity, IAudited
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
    public string AuditLabel => $"Role {Name}";
}

public class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>Grants a role to a user, optionally limited to one site and/or department. Null scope means all.</summary>
public class UserRoleAssignment : Entity, IAudited
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string AuditLabel => $"role assignment {AuditRef.To(RoleId, "")} for {AuditRef.To(UserId, "a person")}{(SiteId is { } s ? " at site " + AuditRef.To(s, "") : "")}";
}
