using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Saved chargeback reports. Viewing needs reports access; creating and editing need the chargeback permission; deleting needs delete chargeback reports (checked in the service).</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/chargebacks")]
public sealed class SequflohChargebackController(SequflohChargebackService svc) : ControllerBase
{
    [HttpGet] public Task<object> List([FromQuery] string? q, CancellationToken ct) => svc.ListAsync(q, ct);
    [HttpGet("prefill/{trackingId:guid}")] public Task<object> Prefill(Guid trackingId, CancellationToken ct) => svc.PrefillAsync(trackingId, ct);
    public sealed record LinkedRequest(List<Guid> WorkOrderIds);
    [HttpPost("prefill-linked")] public async Task<object> PrefillLinked(LinkedRequest r, CancellationToken ct) => await svc.LinkedAsync(r.WorkOrderIds ?? [], false, ct);
    [HttpGet("sections")] public object Sections() => SequflohChargebackService.SectionOptions;
    [HttpGet("people")]
    public Task<List<string>> People(Guid? trackingRecordId, string? kind, DateOnly? from, DateOnly? to, string? type, string? subType, Guid? familyId, Guid? siteId, Guid? projectId, CancellationToken ct) =>
        svc.PeopleAsync(trackingRecordId, string.IsNullOrWhiteSpace(kind) ? null : new ReportPeriod(kind, from, to, null, type, subType, familyId, siteId, projectId), ct);
    [HttpGet("{id:guid}/view")] public Task<object> View(Guid id, CancellationToken ct) => svc.ViewAsync(id, ct);
    [HttpGet("{id:guid}")] public Task<object> Get(Guid id, CancellationToken ct) => svc.GetAsync(id, ct);
    [HttpPost] public Task<object> Create(ChargebackInput r, CancellationToken ct) => svc.CreateAsync(r, ct);
    [HttpPut("{id:guid}")] public Task<object> Update(Guid id, ChargebackInput r, CancellationToken ct) => svc.UpdateAsync(id, r, ct);
    public sealed record DeleteManyRequest(List<Guid>? Ids);
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await svc.DeleteAsync(id, ct); return NoContent(); }
    /// <summary>Deletes the listed chargeback reports. One audit entry is written for each report that goes.</summary>
    [HttpPost("delete")] public async Task<IActionResult> DeleteMany(DeleteManyRequest r, CancellationToken ct) { await svc.DeleteManyAsync(r.Ids ?? [], ct); return NoContent(); }
    [HttpGet("{id:guid}/pdf")] public async Task<IActionResult> Pdf(Guid id, CancellationToken ct) { var (b, n) = await svc.PdfAsync(id, ct); return InlinePdf.Send(this, b, n); }
    [HttpGet("{id:guid}/xlsx")]
    public async Task<IActionResult> Xlsx(Guid id, CancellationToken ct) { var (b, n) = await svc.ExcelAsync(id, ct); return File(b, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", n); }
}
