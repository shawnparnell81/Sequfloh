using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Crew time on a work order. Reading is open to anyone who can see the work order; changes need the crew time permission (checked in the service).</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohCrewController(SequflohCrewService crew, SequflohTimeCorrectionService fix) : ControllerBase
{
    public sealed record StartRequest(int CrewSize, Guid? WorkerId = null);
    public sealed record WorkerRequest(string? Name);

    [HttpGet("{id:guid}/crew")] public Task<object> Get(Guid id, CancellationToken ct) => crew.SummaryAsync(id, ct);
    [HttpPost("{id:guid}/crew/start")] public async Task<object> Start(Guid id, StartRequest r, CancellationToken ct) { await crew.StartAsync(id, r.CrewSize, ct, r.WorkerId); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/pause")] public async Task<object> Pause(Guid id, [FromQuery] Guid? workerId, CancellationToken ct) { await crew.PauseAsync(id, ct, workerId); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/continue")] public async Task<object> Continue(Guid id, [FromQuery] Guid? workerId, CancellationToken ct) { await crew.ContinueAsync(id, ct, workerId); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/stop")] public async Task<object> Stop(Guid id, [FromQuery] Guid? workerId, CancellationToken ct) { await crew.StopAsync(id, ct, workerId); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/sessions")] public async Task<object> Add(Guid id, CrewSessionInput r, CancellationToken ct) { await crew.AddAsync(id, r, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPost("{id:guid}/crew/bulk")] public Task<object> Bulk(Guid id, CrewBulkInput r, [FromQuery] bool? preview, CancellationToken ct) => crew.BulkAsync(id, r, preview == true, ct);
    [HttpPost("{id:guid}/crew/workers")] public async Task<object> AddWorker(Guid id, WorkerRequest r, CancellationToken ct) { await crew.AddWorkerAsync(id, r.Name, ct); return await crew.SummaryAsync(id, ct); }
    [HttpPut("crew/workers/{workerId:guid}")] public async Task<IActionResult> RenameWorker(Guid workerId, WorkerRequest r, CancellationToken ct) { await crew.RenameWorkerAsync(workerId, r.Name, ct); return NoContent(); }
    [HttpDelete("crew/workers/{workerId:guid}")] public async Task<IActionResult> RemoveWorker(Guid workerId, CancellationToken ct) { await crew.RemoveWorkerAsync(workerId, ct); return NoContent(); }
    [HttpPut("crew/sessions/{sessionId:guid}")] public async Task<IActionResult> Update(Guid sessionId, CrewSessionInput r, CancellationToken ct) { await crew.UpdateAsync(sessionId, r, ct); return NoContent(); }
    [HttpDelete("crew/sessions/{sessionId:guid}")] public async Task<IActionResult> Delete(Guid sessionId, CancellationToken ct) { await crew.DeleteAsync(sessionId, ct); return NoContent(); }
    [HttpGet("{id:guid}/time-corrections")] public Task<object> Corrections(Guid id, CancellationToken ct) => fix.ListAsync(id, ct);
    [HttpPost("{id:guid}/time-corrections/transfer")] public Task<object> Transfer(Guid id, TimeTransferInput r, CancellationToken ct) => fix.TransferAsync(id, r, ct);
    [HttpPost("{id:guid}/time-corrections/remove")] public Task<object> Remove(Guid id, TimeRemoveInput r, CancellationToken ct) => fix.RemoveAsync(id, r, ct);
}
