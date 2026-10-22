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
    public const string Issuer = "sequfloh";
    public const string CookieName = "sqf.session";
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
}
