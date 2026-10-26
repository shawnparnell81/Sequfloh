using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Sequfloh.Infrastructure.Identity;

namespace Sequfloh.Platform;

public sealed class AuthSettings
{
    /// <summary>Signing secret for session tokens (at least 32 characters). Set JWT_SECRET in production.</summary>
    public string JwtSecret { get; set; } = "";
    public int SessionHours { get; set; } = 12;
    /// <summary>How long "Remember me" lets a person sign in again without their password.</summary>
    public int RememberDays { get; set; } = 30;
    public const string Issuer = "sequfloh";
    public const string CookieName = "sqf.session";
    public const string RememberCookieName = "sqf.remember";
    /// <summary>Remember tokens use their own audience, so the API never accepts one as a session.</summary>
    public const string RememberAudience = "sequfloh-remember";
    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(JwtSecret));
}

public sealed class TokenService(AuthSettings settings)
{
    public (string Token, DateTimeOffset Expires) Issue(ApplicationUser u)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(settings.SessionHours);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Email),
            new Claim("display_name", u.DisplayName),
            new Claim("tv", u.TokenVersion.ToString()),
        };
        var jwt = new JwtSecurityToken(AuthSettings.Issuer, AuthSettings.Issuer, claims, DateTime.UtcNow, expires.UtcDateTime,
            new SigningCredentials(settings.Key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    public (string Token, DateTimeOffset Expires) IssueRemember(ApplicationUser u)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(settings.RememberDays);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim("tv", u.TokenVersion.ToString()) };
        var jwt = new JwtSecurityToken(AuthSettings.Issuer, AuthSettings.RememberAudience, claims, DateTime.UtcNow, expires.UtcDateTime,
            new SigningCredentials(settings.Key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    /// <summary>The user id and token version in a valid remember token, or null.</summary>
    public (Guid UserId, string TokenVersion)? ReadRemember(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var p = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = AuthSettings.Issuer, ValidAudience = AuthSettings.RememberAudience, IssuerSigningKey = settings.Key, ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);
            return Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && p.FindFirstValue("tv") is { } tv ? (id, tv) : null;
        }
        catch (Exception) { return null; }
    }
}
