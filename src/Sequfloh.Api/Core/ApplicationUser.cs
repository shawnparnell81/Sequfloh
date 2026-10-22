namespace Sequfloh.Infrastructure.Identity;

/// <summary>A person who signs in. Passwords are stored only as salted PBKDF2 hashes.</summary>
public class ApplicationUser
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string UserName { get; set; } = "";
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public Guid? DefaultSiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
    /// <summary>Set when an administrator gives a temporary password: the next sign-in must choose a new password first.</summary>
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public int FailedSignIns { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>Raised on password change or deactivation so tokens issued before it stop working.</summary>
    public int TokenVersion { get; set; }
}
