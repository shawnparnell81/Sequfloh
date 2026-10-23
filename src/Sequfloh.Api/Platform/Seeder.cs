using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Organization;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Platform;

/// <summary>
/// Idempotent start-up seed: organization row, default roles with suggested permissions (all editable by an administrator),
/// numbering and dropdown values. A first administrator is only created from configuration when one is supplied.
/// </summary>
public sealed class Seeder(AppDbContext db, IConfiguration config, IPasswordHasher<ApplicationUser> hasher)
{
    public const string AdministratorRole = "Administrator";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!await db.OrganizationSettings.AnyAsync(ct))
            db.OrganizationSettings.Add(new OrganizationSettings { ApplicationName = "SEQUFLOH", OrganizationName = config["Seed:OrganizationName"] ?? "" });
        await EnsureRoleAsync(AdministratorRole, "Full access, including people, roles and settings.", Permissions.All, ct);
        foreach (var s in SequflohCatalog.DefaultRoleSuggestions())
            await EnsureRoleAsync(s.Name, s.Description, [.. s.Permissions, Permissions.MasterDataView], ct);
        await db.SaveChangesAsync(ct);
        await SequflohFoundationService.SeedAsync(db, ct);
        await SequflohInspectionTemplates.SeedAsync(db, ct);

        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password) && !await db.Users.AnyAsync(ct))
        {
            var site = await db.Sites.FirstOrDefaultAsync(ct);
            if (site is null) { site = new Site { Code = config["Seed:SiteCode"] ?? "MAIN", Name = config["Seed:SiteName"] ?? "Main site" }; db.Sites.Add(site); }
            await CreateAdministratorAsync(email, config["Seed:AdminName"] ?? "Administrator", password, site.Id, config.GetValue("Seed:AdminMustChangePassword", true), ct);
        }
    }

    public async Task<ApplicationUser> CreateAdministratorAsync(string email, string name, string password, Guid siteId, bool mustChange, CancellationToken ct)
    {
        var u = new ApplicationUser { Email = email.Trim(), UserName = email.Trim(), NormalizedEmail = email.Trim().ToUpperInvariant(), DisplayName = name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow, DefaultSiteId = siteId, MustChangePassword = mustChange };
        u.PasswordHash = hasher.HashPassword(u, password);
        db.Users.Add(u);
        var role = await db.QmsRoles.FirstAsync(r => r.Name == AdministratorRole, ct);
        db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = u.Id, RoleId = role.Id });
        await db.SaveChangesAsync(ct);
        return u;
    }

    async Task EnsureRoleAsync(string name, string description, IEnumerable<string> permissions, CancellationToken ct)
    {
        if (await db.QmsRoles.AnyAsync(r => r.Name == name, ct) || db.QmsRoles.Local.Any(r => r.Name == name)) return;
        var role = new Role { Name = name, Description = description, IsSystem = name == AdministratorRole };
        role.Permissions = permissions.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }).ToList();
        db.QmsRoles.Add(role);
    }
}
