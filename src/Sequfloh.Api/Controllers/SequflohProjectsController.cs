using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Projects. Viewing and picking need SEQUFLOH access; creating, editing, closing and deleting need the projects permission (checked in the service).</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/projects")]
public sealed class SequflohProjectsController(SequflohProjectService svc) : ControllerBase
{
    [HttpGet] public Task<object> List([FromQuery] string? status, [FromQuery] string? q, CancellationToken ct) => svc.ListAsync(status, q, ct);
    [HttpGet("{id:guid}")] public Task<object> Get(Guid id, CancellationToken ct) => svc.GetAsync(id, ct);
    [HttpGet("open-summary")] public Task<object> OpenSummary(CancellationToken ct) => svc.OpenSummaryAsync(ct);
    [HttpPost] public Task<object> Create(ProjectInput r, CancellationToken ct) => svc.CreateAsync(r, ct);
    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, ProjectInput r, CancellationToken ct) { await svc.UpdateAsync(id, r, ct); return NoContent(); }
    [HttpPost("{id:guid}/close")] public async Task<IActionResult> Close(Guid id, CancellationToken ct) { await svc.SetStatusAsync(id, ProjectStatus.Closed, ct); return NoContent(); }
    [HttpPost("{id:guid}/reopen")] public async Task<IActionResult> Reopen(Guid id, CancellationToken ct) { await svc.SetStatusAsync(id, ProjectStatus.Open, ct); return NoContent(); }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await svc.DeleteAsync(id, ct); return NoContent(); }
    [HttpPut("work-orders/{trackingId:guid}")] public async Task<IActionResult> LinkWorkOrder(Guid trackingId, ProjectLinkInput r, CancellationToken ct) { await svc.LinkWorkOrderAsync(trackingId, r.ProjectId, ct); return NoContent(); }
    [HttpPut("inspection-requests/{requestId:guid}")] public async Task<IActionResult> LinkRequest(Guid requestId, ProjectLinkInput r, CancellationToken ct) { await svc.LinkRequestAsync(requestId, r.ProjectId, ct); return NoContent(); }
}
