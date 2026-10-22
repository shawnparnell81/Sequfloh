using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Inspection requests. Anyone signed in to SEQUFLOH can submit, view and update them.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/inspection-requests")]
public sealed class SequflohInspectionController(SequflohInspectionService s) : ControllerBase
{
    public sealed record StatusInput(InspectionRequestStatus Status);
    [HttpGet] public Task<object> List([FromQuery] string? status, [FromQuery] string? q, CancellationToken ct) => s.ListAsync(status, q, ct);
    [HttpGet("{id:guid}")] public Task<object> Get(Guid id, CancellationToken ct) => s.GetAsync(id, ct);
    [HttpGet("{id:guid}/history")] public Task<object> History(Guid id, CancellationToken ct) => s.HistoryAsync(id, ct);
    [HttpPost] public Task<object> Create(InspectionRequestInput r, CancellationToken ct) => s.CreateAsync(r, ct);
    [HttpPut("{id:guid}")] public Task<object> Update(Guid id, InspectionRequestInput r, CancellationToken ct) => s.UpdateAsync(id, r, ct);
    [HttpPost("{id:guid}/status")] public Task<object> Status(Guid id, StatusInput r, CancellationToken ct) => s.SetStatusAsync(id, r.Status, ct);
    [HttpPost("{id:guid}/convert")] public Task<object> Convert(Guid id, InspectionConvertInput r, CancellationToken ct) => s.ConvertAsync(id, r, ct);
}
