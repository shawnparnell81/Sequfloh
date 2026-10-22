using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>
/// Live time and assigned people on a work order. Time needs no permission: every signed-in person can start and stop their own timer on any work order.
/// </summary>
[ApiController, Authorize, Route("api/sequfloh/tracking/{id:guid}")]
public sealed class SequflohTimeController(SequflohTimeService time, SequflohTrackingService tracking, AppDbContext db) : ControllerBase
{
    public sealed record AssigneesRequest(List<Guid>? UserIds);

    [HttpGet("time")] public Task<object> Time(Guid id, CancellationToken ct) => time.SummaryAsync(id, ct);
    [HttpPost("time/start")] public async Task<object> Start(Guid id, CancellationToken ct) { await time.StartAsync(id, ct); return await time.SummaryAsync(id, ct); }
    [HttpPost("time/stop")] public async Task<object> Stop(Guid id, CancellationToken ct) { await time.StopAsync(id, ct); return await time.SummaryAsync(id, ct); }

    [HttpGet("assignees")]
    public async Task<object> Assignees(Guid id, CancellationToken ct) =>
        await db.SequflohTaskAssignees.AsNoTracking().Where(a => a.TrackingRecordId == id).OrderBy(a => a.AddedAt)
            .Join(db.Users, a => a.UserId, u => u.Id, (a, u) => new { u.Id, u.DisplayName }).ToListAsync(ct);

    [HttpPut("assignees")]
    public async Task<object> SetAssignees(Guid id, AssigneesRequest r, CancellationToken ct) { await tracking.SetAssigneesAsync(id, r.UserIds ?? [], ct); return await Assignees(id, ct); }
}
