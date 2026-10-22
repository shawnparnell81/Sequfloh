using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Reusable work instruction templates: list, read, create, change, retire and restore.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/instruction-templates")]
public sealed class SequflohTemplatesController(SequflohTemplateService templates) : ControllerBase
{
    static object Dto(SequflohInstructionTemplate t) => new { t.Id, t.Name, t.Description, t.Type, t.IsActive, t.OwnerId, t.RowVersion, t.UpdatedAt, Blocks = JsonNode.Parse(t.Blocks) };

    [HttpGet]
    public async Task<object> List([FromQuery] WorkInstructionType? type, [FromQuery] bool includeRetired = false, CancellationToken ct = default) =>
        (await templates.ListAsync(type, includeRetired, ct)).Select(Dto);

    [HttpGet("{id:guid}")] public async Task<object> Get(Guid id, CancellationToken ct) => Dto(await templates.GetAsync(id, ct));

    [HttpPost] [RequirePermission(Permissions.SequflohCreateWorkInstruction)]
    public async Task<object> Create(InstructionTemplateInput r, CancellationToken ct) => Dto(await templates.CreateAsync(r, ct));

    [HttpPut("{id:guid}")]
    public async Task<object> Update(Guid id, InstructionTemplateInput r, CancellationToken ct) => Dto(await templates.UpdateAsync(id, r, ct));

    [HttpPost("{id:guid}/retire")]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct) { await templates.SetActiveAsync(id, false, ct); return NoContent(); }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) { await templates.SetActiveAsync(id, true, ct); return NoContent(); }
}
