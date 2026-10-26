using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Organization;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Platform;

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SetupRequest(string OrganizationName, string SiteCode, string SiteName, string TimeZone, string DisplayName, string Email, string Password);

[ApiController, Route("api/auth")]
public sealed class AuthController(AppDbContext db, IPasswordHasher<ApplicationUser> hasher, TokenService tokens, IAccessService access, Seeder seeder, IWebHostEnvironment env, AuthSettings authSettings) : ControllerBase
{
    public const int MinPasswordLength = 10;
    const int MaxFailures = 5;

    void Audit(Guid? userId, string userName, string action, string description) =>
        db.AuditEntries.Add(new AuditEntry { OccurredAt = DateTimeOffset.UtcNow, UserId = userId, UserName = userName, Action = action, EntityType = "SignIn", EntityId = userId ?? Guid.Empty,
            Description = description, CorrelationId = HttpContext.TraceIdentifier });

    public static void CheckPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength) throw new AppException($"Passwords must be at least {MinPasswordLength} characters.");
        if (password.Length > 200) throw new AppException("Passwords are limited to 200 characters.");
    }

    bool SecureCookie => !env.IsDevelopment() && !env.IsEnvironment("Testing") && Request.IsHttps;

    // Always a session cookie (no Expires): closing the browser signs the person out, and the token expires after SessionHours.
    void SetCookie(string token) =>
        Response.Cookies.Append(AuthSettings.CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = SecureCookie, Path = "/" });

    // "Remember me": lasts RememberDays and only lets the sign-in page offer "Sign in as"; the API never accepts it as a session.
    void SetRememberCookie(ApplicationUser u)
    {
        var (token, expires) = tokens.IssueRemember(u);
        Response.Cookies.Append(AuthSettings.RememberCookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = SecureCookie, Path = "/api/auth", Expires = expires });
    }

    void ClearRememberCookie() => Response.Cookies.Delete(AuthSettings.RememberCookieName, new CookieOptions { Path = "/api/auth" });

    async Task<ApplicationUser?> RememberedUserAsync(CancellationToken ct)
    {
        if (tokens.ReadRemember(Request.Cookies[AuthSettings.RememberCookieName]) is not { } r) return null;
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == r.UserId, ct);
        return u is { IsActive: true } && u.TokenVersion.ToString() == r.TokenVersion ? u : null;
    }

    /// <summary>True until the first administrator has been created.</summary>
    [HttpGet("setup"), AllowAnonymous]
    public async Task<object> SetupStatus(CancellationToken ct) => new { needsSetup = !await db.Users.AnyAsync(ct) };

    /// <summary>First run only: creates the organization, the first site and the first administrator, then signs them in.</summary>
    [HttpPost("setup"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Setup(SetupRequest r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await db.Users.AnyAsync(ct)) return Conflict(new { title = "Set-up is already complete. Sign in instead." });
        if (string.IsNullOrWhiteSpace(r.OrganizationName) || string.IsNullOrWhiteSpace(r.DisplayName) || string.IsNullOrWhiteSpace(r.SiteCode) || string.IsNullOrWhiteSpace(r.SiteName))
            throw new AppException("Organization, site and your name are required.");
        if (!System.Net.Mail.MailAddress.TryCreate(r.Email?.Trim(), out _)) throw new AppException("Enter a valid email address.");
        CheckPassword(r.Password);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZone ?? "", out _) ? r.TimeZone! : "America/New_York";
        var org = await db.OrganizationSettings.FirstAsync(ct);
        org.OrganizationName = r.OrganizationName.Trim(); org.TimeZone = zone;
        var site = new Site { Code = r.SiteCode.Trim().ToUpperInvariant(), Name = r.SiteName.Trim(), TimeZone = zone };
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        var u = await seeder.CreateAdministratorAsync(r.Email!, r.DisplayName, r.Password, site.Id, false, ct);
        Audit(u.Id, u.DisplayName, "SetupCompleted", $"{u.DisplayName} completed first-run set-up for {org.OrganizationName} and became the first administrator");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var (token, expires) = tokens.Issue(u);
        SetCookie(token);
        return Ok(new { token, expires });
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest r, CancellationToken ct)
    {
        var email = (r.Email ?? "").Trim().ToUpperInvariant();
        var u = await db.Users.FirstOrDefaultAsync(x => x.NormalizedEmail == email, ct);
        var now = DateTimeOffset.UtcNow;
        if (u is null || !u.IsActive)
        {
            Audit(null, r.Email ?? "", "SignInFailed", $"Failed sign-in for {r.Email}: unknown or inactive account");
            await db.SaveChangesAsync(ct);
            return Unauthorized(new { title = "Email or password is incorrect." });
        }
        if (u.LockedUntil is { } until && until > now)
            return Unauthorized(new { title = "This account is locked for a few minutes after repeated failed sign-ins. Try again later." });
        var result = string.IsNullOrEmpty(u.PasswordHash) ? PasswordVerificationResult.Failed : hasher.VerifyHashedPassword(u, u.PasswordHash, r.Password ?? "");
        if (result == PasswordVerificationResult.Failed)
        {
            u.FailedSignIns++;
            if (u.FailedSignIns >= MaxFailures) { u.LockedUntil = now.AddMinutes(15); u.FailedSignIns = 0; }
            Audit(u.Id, u.DisplayName, "SignInFailed", $"Failed sign-in for {u.DisplayName}: wrong password{(u.LockedUntil > now ? "; account locked for 15 minutes" : "")}");
            await db.SaveChangesAsync(ct);
            return Unauthorized(new { title = "Email or password is incorrect." });
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded) u.PasswordHash = hasher.HashPassword(u, r.Password!);
        u.FailedSignIns = 0; u.LockedUntil = null; u.LastSignInAt = now;
        Audit(u.Id, u.DisplayName, "SignedIn", r.RememberMe ? $"{u.DisplayName} signed in and ticked Remember me (no password needed for {authSettings.RememberDays} days on this device)" : $"{u.DisplayName} signed in");
        await db.SaveChangesAsync(ct);
        var (token, expires) = tokens.Issue(u);
        SetCookie(token);
        if (r.RememberMe) SetRememberCookie(u); else ClearRememberCookie();
        return Ok(new { token, expires, mustChangePassword = u.MustChangePassword });
    }

    /// <summary>Who "Remember me" remembers on this device, for the "Sign in as" button; never signs anyone in.</summary>
    [HttpGet("remembered"), AllowAnonymous]
    public async Task<object> Remembered(CancellationToken ct)
    {
        var u = await RememberedUserAsync(ct);
        if (u is null) { if (Request.Cookies.ContainsKey(AuthSettings.RememberCookieName)) ClearRememberCookie(); return new { remembered = false, displayName = (string?)null, email = (string?)null }; }
        return new { remembered = true, displayName = u.DisplayName, email = u.Email };
    }

    /// <summary>"Sign in as": a session from the remember cookie, without the password.</summary>
    [HttpPost("remembered"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<IActionResult> SignInRemembered(CancellationToken ct)
    {
        var u = await RememberedUserAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (u is null || u.LockedUntil is { } until && until > now)
        {
            ClearRememberCookie();
            return Unauthorized(new { title = "Sign in with your email and password." });
        }
        u.LastSignInAt = now;
        Audit(u.Id, u.DisplayName, "SignedIn", $"{u.DisplayName} signed in with Remember me");
        await db.SaveChangesAsync(ct);
        var (token, expires) = tokens.Issue(u);
        SetCookie(token);
        return Ok(new { token, expires, mustChangePassword = u.MustChangePassword });
    }

    /// <summary>"Use a different account": forgets the remembered person on this device.</summary>
    [HttpPost("forget"), AllowAnonymous]
    public IActionResult Forget()
    {
        ClearRememberCookie();
        return NoContent();
    }

    /// <summary>Ends the session; a "Remember me" cookie stays so the next sign-in can use "Sign in as".</summary>
    [HttpPost("logout"), AllowAnonymous]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(AuthSettings.CookieName, new CookieOptions { Path = "/" });
        return NoContent();
    }

    /// <summary>The signed-in person and their grants; answers 200 when signed out so a cold visit is not an error.</summary>
    [HttpGet("session"), AllowAnonymous]
    public async Task<object> Session(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true || !Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id))
            return new { authenticated = false, user = (object?)null, needsSetup = !await db.Users.AnyAsync(ct) };
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        if (u is null) return new { authenticated = false, user = (object?)null, needsSetup = false };
        var grants = await access.GetGrantsAsync(id, ct);
        return new { authenticated = true, needsSetup = false, user = new { u.Id, u.Email, u.DisplayName, u.MustChangePassword, Grants = grants } };
    }

    [HttpPost("change-password"), Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest r, CancellationToken ct)
    {
        var id = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var u = await db.Users.FirstAsync(x => x.Id == id, ct);
        if (hasher.VerifyHashedPassword(u, u.PasswordHash, r.CurrentPassword ?? "") == PasswordVerificationResult.Failed) throw new AppException("The current password is incorrect.");
        CheckPassword(r.NewPassword);
        if (r.NewPassword == r.CurrentPassword) throw new AppException("Choose a password you have not just used.");
        u.PasswordHash = hasher.HashPassword(u, r.NewPassword); u.MustChangePassword = false; u.PasswordChangedAt = DateTimeOffset.UtcNow; u.TokenVersion++;
        Audit(u.Id, u.DisplayName, "PasswordChanged", $"{u.DisplayName} changed their password");
        await db.SaveChangesAsync(ct);
        var (token, expires) = tokens.Issue(u);
        SetCookie(token);
        if (tokens.ReadRemember(Request.Cookies[AuthSettings.RememberCookieName]) is { } rem && rem.UserId == u.Id) SetRememberCookie(u); // keep Remember me after the version bump
        return Ok(new { token, expires });
    }
}
