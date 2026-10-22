namespace Sequfloh.Application.Security;

/// <summary>A permission granted to the user, limited to a site and/or department when those are set.</summary>
public sealed record PermissionGrant(string Permission, Guid? SiteId, Guid? DepartmentId);

/// <summary>Server-side permission and scope checks. Every module uses this; no module keeps its own rules.</summary>
public interface IAccessService
{
    Task<IReadOnlyList<PermissionGrant>> GetGrantsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when the user holds the permission anywhere (any scope).</summary>
    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default);

    /// <summary>True when the user holds the permission for the given site/department.</summary>
    Task<bool> CanAccessAsync(Guid userId, string permission, Guid siteId, Guid? departmentId, CancellationToken ct = default);

    /// <summary>Scope filter for list/search queries. AllSites=true means no site restriction.</summary>
    Task<AccessScope> GetScopeAsync(Guid userId, string permission, CancellationToken ct = default);

    /// <summary>Active users who hold the permission for the given site/department.</summary>
    Task<IReadOnlyList<Guid>> UsersWithPermissionAsync(string permission, Guid siteId, Guid? departmentId, CancellationToken ct = default);
}

public sealed record AccessScope(bool AllSites, IReadOnlySet<Guid> SiteIds, IReadOnlySet<(Guid Site, Guid Dept)> DepartmentPairs)
{
    public static readonly AccessScope None = new(false, new HashSet<Guid>(), new HashSet<(Guid, Guid)>());
    public bool IsEmpty => !AllSites && SiteIds.Count == 0 && DepartmentPairs.Count == 0;

    public bool Allows(Guid siteId, Guid? departmentId) =>
        AllSites || SiteIds.Contains(siteId) ||
        (departmentId is { } d && DepartmentPairs.Contains((siteId, d)));
}
