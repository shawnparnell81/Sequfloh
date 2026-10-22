using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Common;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Quality;

/// <summary>Common access, scope and validation checks shared by every service.</summary>
public sealed class ModuleSupport(AppDbContext db, IAccessService access, ICurrentUser current, IClock clock)
{
    public Guid UserId => current.UserId ?? throw new ForbiddenException();
    /// <summary>Today in the site's time zone (organization zone when no site is given).</summary>
    public async Task<DateOnly> TodayAsync(Guid? siteId, CancellationToken ct) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, await ZoneAsync(siteId, ct)).DateTime);
    public DateTimeOffset Now => clock.UtcNow;
    public ICurrentUser Current => current;
    public async Task<TimeZoneInfo> ZoneAsync(Guid? siteId, CancellationToken ct)
    {
        var id = siteId is { } s ? await db.Sites.AsNoTracking().Where(x => x.Id == s).Select(x => x.TimeZone).FirstOrDefaultAsync(ct) : null;
        id ??= await db.OrganizationSettings.AsNoTracking().Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        return TimeZoneInfo.TryFindSystemTimeZoneById(id ?? "America/New_York", out var z) ? z : TimeZoneInfo.Utc;
    }
    /// <summary>Instant range covering business days <paramref name="from"/> to <paramref name="to"/> (inclusive) in the site's time zone.</summary>
    public async Task<(DateTimeOffset From, DateTimeOffset To)> DayRangeAsync(Guid? siteId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = await ZoneAsync(siteId, ct);
        static DateTimeOffset Start(DateOnly d, TimeZoneInfo z)
        {
            var local = d.ToDateTime(TimeOnly.MinValue);
            while (z.IsInvalidTime(local)) local = local.AddMinutes(30);
            return new DateTimeOffset(local, z.GetUtcOffset(local)).ToUniversalTime();
        }
        return (Start(from, zone), Start(to.AddDays(1), zone));
    }

    public async Task RequireAsync(string permission, Guid siteId, Guid? departmentId, CancellationToken ct)
    {
        if (!await access.CanAccessAsync(UserId, permission, siteId, departmentId, ct)) throw new ForbiddenException();
    }

    public Task<bool> CanAsync(string permission, Guid siteId, Guid? departmentId, CancellationToken ct) =>
        access.CanAccessAsync(UserId, permission, siteId, departmentId, ct);

    /// <summary>True when the user holds the permission at any site (for organization-wide lists such as carriers).</summary>
    public async Task<bool> CanAnyAsync(string permission, CancellationToken ct) => !(await access.GetScopeAsync(UserId, permission, ct)).IsEmpty;

    /// <summary>True when the user holds the permission organization-wide (for organization defaults).</summary>
    public async Task<bool> HasAllSitesAsync(string permission, CancellationToken ct) => (await access.GetScopeAsync(UserId, permission, ct)).AllSites;

    public async Task ValidateLocationAsync(Guid siteId, Guid? departmentId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId && !s.IsArchived, ct)) throw new AppException("Select an active site.");
        if (departmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.SiteId == siteId, ct)) throw new AppException("The department does not belong to the site.");
    }

    /// <summary>True when another (active) user holds the permission for the site/department.</summary>
    public Task<bool> UserCanAsync(Guid userId, string permission, Guid siteId, Guid? departmentId, CancellationToken ct) => access.CanAccessAsync(userId, permission, siteId, departmentId, ct);
    public Task<IReadOnlyList<Guid>> UsersWithAsync(string permission, Guid siteId, Guid? departmentId, CancellationToken ct) => access.UsersWithPermissionAsync(permission, siteId, departmentId, ct);

    public async Task RequireActiveUserAsync(Guid userId, string role, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct)) throw new AppException($"The {role} must be an active user.");
    }

    /// <summary>Restricts a list query to the sites and departments where the user holds the permission.</summary>
    public async Task<IQueryable<T>> ScopeAsync<T>(IQueryable<T> q, string permission, CancellationToken ct) where T : class, ISiteScoped
    {
        var scope = await access.GetScopeAsync(UserId, permission, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        if (scope.AllSites) return q;
        var sites = scope.SiteIds.ToList();
        var pairs = scope.DepartmentPairs.Select(p => p.Site.ToString() + "|" + p.Dept.ToString()).ToList();
        return q.Where(x => sites.Contains(x.SiteId) || (x.DepartmentId != null && pairs.Contains(x.SiteId.ToString() + "|" + x.DepartmentId.ToString())));
    }

    /// <summary>Like ScopeAsync, but returns null instead of throwing when the user lacks the permission (for related-record panels).</summary>
    public async Task<IQueryable<T>?> TryScopeAsync<T>(IQueryable<T> q, string permission, CancellationToken ct) where T : class, ISiteScoped
    {
        var scope = await access.GetScopeAsync(UserId, permission, ct);
        return scope.IsEmpty ? null : await ScopeAsync(q, permission, ct);
    }


    public static void Required(string? value, string label, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AppException($"{label} is required.");
        if (value.Length > max) throw new AppException($"{label} is limited to {max} characters.");
    }
}

