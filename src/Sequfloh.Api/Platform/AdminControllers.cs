using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Organization;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Platform;

public sealed record CreateUserRequest(string Email, string DisplayName, string Password, bool MustChangePassword = true, string? JobTitle = null, Guid? DefaultSiteId = null, Guid[]? RoleIds = null);
public sealed record UpdateUserRequest(string DisplayName, string? JobTitle, Guid? DefaultSiteId, bool IsActive);
public sealed record ResetPasswordRequest(string Password, bool MustChangePassword = true);
public sealed record AssignRoleRequest(Guid RoleId, Guid? SiteId, Guid? DepartmentId);
public sealed record SaveRoleRequest(string Name, string? Description, string[] Permissions);
public sealed record SaveSiteRequest(string Code, string Name, string? Address, string? TimeZone = null);
public sealed record SaveDepartmentRequest(string Code, string Name);
public sealed record OrganizationRequest(string OrganizationName, string TimeZone);

static class AuditWriter
{
    public static void Write(AppDbContext db, ICurrentUser me, string action, string type, Guid id, string description) =>
        db.AuditEntries.Add(new AuditEntry { OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = action, EntityType = type, EntityId = id, Description = description, CorrelationId = me.CorrelationId });
}

[ApiController, Route("api/admin/users")]
public sealed class UsersController(AppDbContext db, IPasswordHasher<ApplicationUser> hasher, ICurrentUser me) : ControllerBase
{
    [HttpGet, RequirePermission(Permissions.AdminUsers)]
    public async Task<object> List(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        var roles = await db.UserRoleAssignments.AsNoTracking().Join(db.QmsRoles, a => a.RoleId, r => r.Id, (a, r) => new { a.Id, a.UserId, a.RoleId, Role = r.Name, a.SiteId, a.DepartmentId }).ToListAsync(ct);
        return users.Select(u => new { u.Id, u.Email, u.DisplayName, u.JobTitle, u.IsActive, u.DefaultSiteId, u.LastSignInAt, u.MustChangePassword, u.CreatedAt,
            Roles = roles.Where(r => r.UserId == u.Id).Select(r => new { r.Id, r.RoleId, r.Role, r.SiteId, r.DepartmentId }) });
    }

    [HttpPost, RequirePermission(Permissions.AdminUsers)]
    public async Task<Guid> Create(CreateUserRequest r, CancellationToken ct)
    {
        var email = (r.Email ?? "").Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) throw new AppException("Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(r.DisplayName)) throw new AppException("Name is required.");
        AuthController.CheckPassword(r.Password);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == email.ToUpperInvariant(), ct)) throw new ConflictException("Someone already uses this email address.");
        var u = new ApplicationUser { Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(), DisplayName = r.DisplayName.Trim(), JobTitle = r.JobTitle?.Trim(),
            DefaultSiteId = r.DefaultSiteId, CreatedAt = DateTimeOffset.UtcNow, MustChangePassword = r.MustChangePassword };
        u.PasswordHash = hasher.HashPassword(u, r.Password);
        db.Users.Add(u);
        foreach (var roleId in r.RoleIds ?? [])
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = u.Id, RoleId = roleId, SiteId = r.DefaultSiteId });
        AuditWriter.Write(db, me, "Created", "User", u.Id, $"Created user {u.DisplayName} ({u.Email})");
        await db.SaveChangesAsync(ct);
        return u.Id;
    }

    [HttpPut("{id:guid}"), RequirePermission(Permissions.AdminUsers)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("User");
        if (string.IsNullOrWhiteSpace(r.DisplayName)) throw new AppException("Name is required.");
        if (!r.IsActive && id == me.UserId) throw new AppException("You cannot deactivate your own account.");
        var changes = new List<string>();
        if (u.DisplayName != r.DisplayName.Trim()) changes.Add($"name from '{u.DisplayName}' to '{r.DisplayName.Trim()}'");
        if (u.IsActive != r.IsActive) { changes.Add(r.IsActive ? "reactivated" : "deactivated"); u.TokenVersion++; }
        if (u.JobTitle != r.JobTitle) changes.Add("job title");
        if (u.DefaultSiteId != r.DefaultSiteId) changes.Add("default site");
        u.DisplayName = r.DisplayName.Trim(); u.JobTitle = r.JobTitle?.Trim(); u.DefaultSiteId = r.DefaultSiteId; u.IsActive = r.IsActive;
        if (changes.Count > 0) AuditWriter.Write(db, me, "Updated", "User", u.Id, $"Updated user {u.DisplayName}: {string.Join(", ", changes)}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/password"), RequirePermission(Permissions.AdminUsers)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest r, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("User");
        AuthController.CheckPassword(r.Password);
        u.PasswordHash = hasher.HashPassword(u, r.Password); u.MustChangePassword = r.MustChangePassword; u.TokenVersion++; u.LockedUntil = null; u.FailedSignIns = 0;
        AuditWriter.Write(db, me, "PasswordReset", "User", u.Id, $"Set a temporary password for {u.DisplayName}");
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/roles"), RequirePermission(Permissions.AdminUsers)]
    public async Task<Guid> AssignRole(Guid id, AssignRoleRequest r, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, ct)) throw new NotFoundException("User");
        var role = await db.QmsRoles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == r.RoleId, ct) ?? throw new NotFoundException("Role");
        if (r.SiteId is { } s && !await db.Sites.AnyAsync(x => x.Id == s, ct)) throw new AppException("Select a site.");
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.SiteId == r.SiteId, ct)) throw new AppException("The department does not belong to the site.");
        if (r.SiteId is not null && role.Permissions.Any(p => Permissions.GlobalOnly.Contains(p.Permission))) throw new AppException("This role holds organization-wide permissions; assign it for all sites.");
        if (await db.UserRoleAssignments.AnyAsync(a => a.UserId == id && a.RoleId == r.RoleId && a.SiteId == r.SiteId && a.DepartmentId == r.DepartmentId, ct)) throw new ConflictException("The person already holds this role here.");
        var a = new UserRoleAssignment { UserId = id, RoleId = r.RoleId, SiteId = r.SiteId, DepartmentId = r.DepartmentId };
        db.UserRoleAssignments.Add(a);
        await db.SaveChangesAsync(ct);
        return a.Id;
    }

    [HttpDelete("{id:guid}/roles/{assignmentId:guid}"), RequirePermission(Permissions.AdminUsers)]
    public async Task<IActionResult> RemoveRole(Guid id, Guid assignmentId, CancellationToken ct)
    {
        var a = await db.UserRoleAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.UserId == id, ct) ?? throw new NotFoundException("Role assignment");
        if (id == me.UserId)
        {
            var admin = await db.QmsRoles.Where(r => r.Name == Seeder.AdministratorRole).Select(r => r.Id).FirstOrDefaultAsync(ct);
            if (a.RoleId == admin) throw new AppException("You cannot remove your own Administrator role.");
        }
        db.UserRoleAssignments.Remove(a);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController, Route("api/users")]
public sealed class UserLookupController(AppDbContext db) : ControllerBase
{
    /// <summary>Active people, for assignment pickers.</summary>
    [HttpGet("lookup")]
    public async Task<object> Lookup(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(ct);
}

[ApiController, Route("api/admin/roles")]
public sealed class RolesController(AppDbContext db) : ControllerBase
{
    [HttpGet, RequirePermission(Permissions.AdminRoles)]
    public async Task<object> List(CancellationToken ct) =>
        (await db.QmsRoles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct))
        .Select(r => new { r.Id, r.Name, r.Description, r.IsSystem, r.RowVersion, Permissions = r.Permissions.Select(p => p.Permission).OrderBy(p => p), Users = db.UserRoleAssignments.Count(a => a.RoleId == r.Id) });

    /// <summary>Every permission code with a readable group and label for the role editor.</summary>
    [HttpGet("permissions")]
    public object Catalog() => Permissions.All;

    [HttpGet("catalog")]
    public object DescribedCatalog() => Permissions.All.Select(p => new { Code = p, Group = PermissionText.Group(p), Label = PermissionText.Label(p) });

    void Validate(SaveRoleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100) throw new AppException("Role name is required (up to 100 characters).");
        var unknown = (r.Permissions ?? []).Where(p => !Permissions.All.Contains(p)).ToList();
        if (unknown.Count > 0) throw new AppException("Unknown permission: " + string.Join(", ", unknown));
    }

    [HttpPost, RequirePermission(Permissions.AdminRoles)]
    public async Task<Guid> Create(SaveRoleRequest r, CancellationToken ct)
    {
        Validate(r);
        if (await db.QmsRoles.AnyAsync(x => x.Name == r.Name.Trim(), ct)) throw new ConflictException("A role with this name already exists.");
        var role = new Role { Name = r.Name.Trim(), Description = r.Description?.Trim() };
        role.Permissions = (r.Permissions ?? []).Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }).ToList();
        db.QmsRoles.Add(role);
        await db.SaveChangesAsync(ct);
        return role.Id;
    }

    [HttpPut("{id:guid}"), RequirePermission(Permissions.AdminRoles)]
    public async Task<IActionResult> Update(Guid id, SaveRoleRequest r, CancellationToken ct)
    {
        Validate(r);
        var role = await db.QmsRoles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Role");
        if (await db.QmsRoles.AnyAsync(x => x.Name == r.Name.Trim() && x.Id != id, ct)) throw new ConflictException("A role with this name already exists.");
        var wanted = (r.Permissions ?? []).Distinct().ToHashSet();
        if (role.Name == Seeder.AdministratorRole && (!wanted.Contains(Permissions.AdminRoles) || !wanted.Contains(Permissions.AdminUsers)))
            throw new AppException("The Administrator role must keep the people and roles permissions so someone can always manage access.");
        var before = role.Permissions.Select(p => p.Permission).ToHashSet();
        role.Name = r.Name.Trim(); role.Description = r.Description?.Trim();
        role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        foreach (var p in wanted.Where(p => !before.Contains(p))) role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        var added = wanted.Except(before).ToList(); var removed = before.Except(wanted).ToList();
        if (added.Count + removed.Count > 0)
            db.AuditEntries.Add(new AuditEntry { OccurredAt = DateTimeOffset.UtcNow, UserId = HttpContext.RequestServices.GetRequiredService<ICurrentUser>().UserId, UserName = HttpContext.RequestServices.GetRequiredService<ICurrentUser>().UserName,
                Action = "PermissionsChanged", EntityType = nameof(Role), EntityId = role.Id,
                Description = $"Changed permissions of role {role.Name}" + (added.Count > 0 ? "; added " + string.Join(", ", added.Select(PermissionText.Label)) : "") + (removed.Count > 0 ? "; removed " + string.Join(", ", removed.Select(PermissionText.Label)) : "") });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}"), RequirePermission(Permissions.AdminRoles)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var role = await db.QmsRoles.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Role");
        if (role.Name == Seeder.AdministratorRole) throw new AppException("The Administrator role cannot be deleted.");
        if (await db.UserRoleAssignments.AnyAsync(a => a.RoleId == id, ct)) throw new ConflictException("People still hold this role. Remove it from them first.");
        db.QmsRoles.Remove(role);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

[ApiController, Route("api/organization")]
public sealed class OrganizationController(AppDbContext db) : ControllerBase
{
    [HttpGet("branding"), Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public async Task<object> Branding(CancellationToken ct)
    {
        var o = await db.OrganizationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new { ApplicationName = o?.ApplicationName ?? "SEQUFLOH", OrganizationName = o?.OrganizationName ?? "", PrimaryColor = (string?)null, HasLogo = false, TimeZone = o?.TimeZone ?? "America/New_York" };
    }

    [HttpPut("settings"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<IActionResult> Save(OrganizationRequest r, CancellationToken ct)
    {
        var o = await db.OrganizationSettings.FirstAsync(ct);
        if (string.IsNullOrWhiteSpace(r.OrganizationName)) throw new AppException("Organization name is required.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone ?? "", out _)) throw new AppException("Choose a valid time zone.");
        o.OrganizationName = r.OrganizationName.Trim(); o.TimeZone = r.TimeZone!;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("sites")]
    public async Task<object> Sites(CancellationToken ct) =>
        await db.Sites.AsNoTracking().OrderBy(s => s.Code).Select(s => new { s.Id, s.Code, s.Name, s.Address, s.TimeZone, s.IsArchived,
            Departments = s.Departments.OrderBy(d => d.Code).Select(d => new { d.Id, d.Code, d.Name, d.IsArchived }) }).ToListAsync(ct);

    [HttpPost("sites"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<Guid> CreateSite(SaveSiteRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Site code and name are required.");
        if (await db.Sites.AnyAsync(s => s.Code == r.Code.Trim(), ct)) throw new ConflictException("That site code already exists.");
        var zone = r.TimeZone ?? await db.OrganizationSettings.Select(o => o.TimeZone).FirstOrDefaultAsync(ct) ?? "America/New_York";
        var s = new Site { Code = r.Code.Trim(), Name = r.Name.Trim(), Address = r.Address?.Trim(), TimeZone = zone };
        db.Sites.Add(s);
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    [HttpPut("sites/{id:guid}"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<IActionResult> UpdateSite(Guid id, SaveSiteRequest r, CancellationToken ct)
    {
        var s = await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Site");
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Site code and name are required.");
        s.Code = r.Code.Trim(); s.Name = r.Name.Trim(); s.Address = r.Address?.Trim();
        if (r.TimeZone is { } z && TimeZoneInfo.TryFindSystemTimeZoneById(z, out _)) s.TimeZone = z;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("sites/{id:guid}/departments"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<Guid> CreateDepartment(Guid id, SaveDepartmentRequest r, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == id, ct)) throw new NotFoundException("Site");
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Department code and name are required.");
        if (await db.Departments.AnyAsync(d => d.SiteId == id && d.Code == r.Code.Trim(), ct)) throw new ConflictException("That department code already exists at this site.");
        var d = new Department { SiteId = id, Code = r.Code.Trim(), Name = r.Name.Trim() };
        db.Departments.Add(d);
        await db.SaveChangesAsync(ct);
        return d.Id;
    }
}

/// <summary>Readable names for permission codes, derived from the code so new permissions need no extra text.</summary>
public static class PermissionText
{
    static readonly Dictionary<string, string> Groups = new()
    {
        ["admin"] = "Administration", ["audittrail"] = "Audit trail", ["parts"] = "Part master", ["sequfloh"] = "Job tracking",
    };

    public static string Group(string code) => Groups.GetValueOrDefault(code.Split('.')[0], code.Split('.')[0]);

    public static string Label(string code)
    {
        var tail = code.Contains('.') ? code[(code.IndexOf('.') + 1)..] : code;
        var words = tail.Replace('_', ' ');
        return code switch
        {
            "admin.users" => "Manage people",
            "admin.roles" => "Manage roles and permissions",
            "admin.organization" => "Manage organization and sites",
            "audittrail.view" => "View the full audit trail",
            "parts.view" => "View parts",
            "parts.edit" => "Add and edit parts",
            "sequfloh.access" => "Open job tracking",
            "sequfloh.view_imported_files" => "View imported Excel and Word files",
            "sequfloh.import_files" => "Import and edit Excel and Word files",
            "sequfloh.view_drawings" => "View drawings",
            "sequfloh.manage_drawings" => "Add drawings, revisions and drawing folders",
            _ => char.ToUpperInvariant(words[0]) + words[1..],
        };
    }
}

/// <summary>Lets an administrator test the file bucket: saves a tiny file, reads it back and reports the provider's answer.</summary>
[ApiController, Route("api/admin/storage-check")]
public sealed class StorageCheckController(Sequfloh.Platform.IFileStorage storage, Sequfloh.Platform.StorageOptions options) : ControllerBase
{
    [HttpPost, RequirePermission(Permissions.AdminOrganization)]
    public async Task<object> Check(CancellationToken ct)
    {
        var key = $"storage-check/{Guid.NewGuid():N}.txt";
        try
        {
            await storage.PutAsync(key, new MemoryStream("ok"u8.ToArray()), "text/plain", ct);
            await using var back = await storage.GetAsync(key, ct);
            return new { Ok = back is not null, options.Provider, Message = back is null ? "The file was saved but could not be read back." : "File storage is working." };
        }
        catch (AppException e) { return new { Ok = false, options.Provider, e.Message }; }
    }
}
