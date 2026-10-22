using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Security;

public sealed class AccessService(AppDbContext db) : IAccessService
{
    private readonly Dictionary<Guid, IReadOnlyList<PermissionGrant>> _cache = new();

    public async Task<IReadOnlyList<PermissionGrant>> GetGrantsAsync(Guid userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached)) return cached;
        var active = await db.Users.Where(u => u.Id == userId).Select(u => u.IsActive).FirstOrDefaultAsync(ct);
        if (!active) return _cache[userId] = [];
        var grants = await (
            from a in db.UserRoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            where a.UserId == userId
            select new PermissionGrant(p.Permission, a.SiteId, a.DepartmentId)).Distinct().ToListAsync(ct);
        return _cache[userId] = grants;
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default) =>
        (await GetGrantsAsync(userId, ct)).Any(g => g.Permission == permission);

    public async Task<bool> CanAccessAsync(Guid userId, string permission, Guid siteId, Guid? departmentId, CancellationToken ct = default) =>
        (await GetScopeAsync(userId, permission, ct)).Allows(siteId, departmentId);

    public async Task<AccessScope> GetScopeAsync(Guid userId, string permission, CancellationToken ct = default) =>
        AccessResolver.Resolve(await GetGrantsAsync(userId, ct), permission);

    public async Task<IReadOnlyList<Guid>> UsersWithPermissionAsync(string permission, Guid siteId, Guid? departmentId, CancellationToken ct = default) =>
        await (
            from a in db.UserRoleAssignments
            join p in db.RolePermissions on a.RoleId equals p.RoleId
            join u in db.Users on a.UserId equals u.Id
            where p.Permission == permission && u.IsActive &&
                  (a.SiteId == null || (a.SiteId == siteId && (a.DepartmentId == null || a.DepartmentId == departmentId)))
            select a.UserId).Distinct().ToListAsync(ct);
}
