namespace Sequfloh.Application.Security;

/// <summary>Pure scope resolution so it can be unit tested without a database.</summary>
public static class AccessResolver
{
    public static AccessScope Resolve(IEnumerable<PermissionGrant> grants, string permission)
    {
        var all = false;
        var sites = new HashSet<Guid>();
        var depts = new HashSet<(Guid, Guid)>();
        foreach (var g in grants.Where(g => g.Permission == permission))
        {
            if (g.SiteId is null) { all = true; break; }
            if (g.DepartmentId is null) sites.Add(g.SiteId.Value);
            else depts.Add((g.SiteId.Value, g.DepartmentId.Value));
        }
        return all ? new AccessScope(true, new HashSet<Guid>(), new HashSet<(Guid, Guid)>()) : new AccessScope(false, sites, depts);
    }
}
