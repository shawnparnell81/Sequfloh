using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Api.Controllers;

/// <summary>A cheap heartbeat and a lookup of who is active, for the avatar dots. Any signed-in person can ask; it returns only the last-seen time.</summary>
[ApiController, Authorize, Route("api/presence")]
public sealed class PresenceController(AppDbContext db, ICurrentUser me) : ControllerBase
{
    public const int MaxIds = 80;

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(CancellationToken ct)
    {
        if (me.UserId is not { } id) return Unauthorized();
        if (Presence.Due(id))
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, DateTimeOffset.UtcNow), ct);
        return NoContent();
    }

    [HttpGet]
    public async Task<object> Query([FromQuery] string? ids, CancellationToken ct)
    {
        var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(MaxIds).ToArray();
        if (wanted.Length == 0) return Array.Empty<object>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).Select(u => new { u.Id, u.LastSeenAt }).ToListAsync(ct);
        return rows.Select(u => new { u.Id, u.LastSeenAt, Online = Presence.IsOnline(u.LastSeenAt, now) });
    }
}
