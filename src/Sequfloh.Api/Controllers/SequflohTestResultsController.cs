using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Test work instruction results on Test work orders: the drawing spec once per order and each part's own results.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohTestResultsController(SequflohTestResultService tests) : ControllerBase
{
    [HttpGet("{id:guid}/test")] public Task<object> Get(Guid id, CancellationToken ct) => tests.GetAsync(id, ct);
    [HttpPut("{id:guid}/test/spec")] public async Task<object> Spec(Guid id, TestSpecInput r, CancellationToken ct) { await tests.SaveSpecAsync(id, r, ct); return await tests.GetAsync(id, ct); }
    [HttpPost("{id:guid}/test/fields")] public async Task<object> AddField(Guid id, TestFieldInput r, CancellationToken ct) { await tests.AddFieldAsync(id, r, ct); return await tests.GetAsync(id, ct); }
    [HttpPut("{id:guid}/test/fields/{key}")] public async Task<object> UpdateField(Guid id, string key, TestFieldInput r, CancellationToken ct) { await tests.UpdateFieldAsync(id, key, r, ct); return await tests.GetAsync(id, ct); }
    [HttpPost("{id:guid}/test/fields/{key}/remove")] public async Task<object> RemoveField(Guid id, string key, TestRemoveInput r, CancellationToken ct) { await tests.RemoveFieldAsync(id, key, r, ct); return await tests.GetAsync(id, ct); }
    [HttpPost("{id:guid}/test/parts")] public Task<object> AddPart(Guid id, TestPartInput r, CancellationToken ct) => tests.AddPartAsync(id, r, ct);
    [HttpPut("test/parts/{partId:guid}")] public async Task<IActionResult> UpdatePart(Guid partId, TestPartInput r, CancellationToken ct) { await tests.UpdatePartAsync(partId, r, ct); return NoContent(); }
    [HttpPut("test/parts/{partId:guid}/result")] public async Task<IActionResult> SetResult(Guid partId, TestResultInput r, CancellationToken ct) { await tests.SetResultAsync(partId, r, ct); return NoContent(); }
    [HttpPost("test/parts/{partId:guid}/remove")] public async Task<IActionResult> RemovePart(Guid partId, TestRemoveInput r, CancellationToken ct) { await tests.RemovePartAsync(partId, r, ct); return NoContent(); }
    [HttpPost("{id:guid}/drawings")] public async Task<IActionResult> AttachDrawing(Guid id, DrawingAttachInput r, CancellationToken ct) { await tests.AttachDrawingAsync(id, r.DrawingId, ct); return NoContent(); }
}
