using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Crew time on a work order. Reading is open to anyone who can see the work order; changes need the crew time permission (checked in the service).</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohCrewController(SequflohCrewService crew) : ControllerBase
{
    public sealed record StartRequest(int CrewSize);

    [HttpGet("{id:guid}/crew")] public Task<object> Get(Guid id, CancellationToken ct) => crew.SummaryAsync(id, ct);
    [HttpPost("{id:guid}/crew/start")] public async Task<object> Start(Guid id, StartRequest r, CancellationToken ct) { await crew.StartAsync(id, r.CrewSize, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/pause")] public async Task<object> Pause(Guid id, CancellationToken ct) { await crew.PauseAsync(id, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/continue")] public async Task<object> Continue(Guid id, CancellationToken ct) { await crew.ContinueAsync(id, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/stop")] public async Task<object> Stop(Guid id, CancellationToken ct) { await crew.StopAsync(id, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/sessions")] public async Task<object> Add(Guid id, CrewSessionInput r, CancellationToken ct) { await crew.AddAsync(id, r, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/bulk")] public Task<object> Bulk(Guid id, CrewBulkInput r, [FromQuery] bool? preview, CancellationToken ct) => crew.BulkAsync(id, r, preview == true, ct);
    [HttpPut("crew/sessions/{sessionId:guid}")] public async Task<IActionResult> Update(Guid sessionId, CrewSessionInput r, CancellationToken ct) { await crew.UpdateAsync(sessionId, r, ct); return NoContent(); }
    [HttpDelete("crew/sessions/{sessionId:guid}")] public async Task<IActionResult> Delete(Guid sessionId, CancellationToken ct) { await crew.DeleteAsync(sessionId, ct); return NoContent(); }
}
