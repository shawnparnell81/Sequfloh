using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;

namespace Sequfloh.Deletion;

/// <summary>
/// Delete for every kind of record people add, import or create. Each needs "Delete records" for the record's site and department (checked in the service);
/// sites and departments need "Manage organization and sites".
/// </summary>
[ApiController, Authorize]
public sealed class DeleteController(DeletionService delete) : ControllerBase
{
    const string P = Permissions.SequflohDeleteRecords;

    [HttpDelete("api/sequfloh/instructions/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> WorkInstruction(Guid id, CancellationToken ct) { await delete.WorkInstructionAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/tracking/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> WorkOrder(Guid id, CancellationToken ct) { await delete.WorkOrderAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/parts/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> Part(Guid id, CancellationToken ct) { await delete.PartAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/parts-lists/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> PartsList(Guid id, CancellationToken ct) { await delete.PartsListAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/operation-steps/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> OperationStep(Guid id, CancellationToken ct) { await delete.OperationStepAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/instruction-templates/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> Template(Guid id, CancellationToken ct) { await delete.TemplateAsync(id, ct); return NoContent(); }

    [HttpDelete("api/drawings/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> Drawing(Guid id, CancellationToken ct) { await delete.DrawingAsync(id, ct); return NoContent(); }

    [HttpDelete("api/drawings/{id:guid}/revisions/{revision}"), RequirePermission(P)]
    public async Task<IActionResult> DrawingRevision(Guid id, string revision, CancellationToken ct) { await delete.DrawingRevisionAsync(id, revision, ct); return NoContent(); }

    [HttpDelete("api/drawings/folders/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> DrawingFolder(Guid id, CancellationToken ct) { await delete.DrawingFolderAsync(id, ct); return NoContent(); }

    [HttpDelete("api/imported-files/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> ImportedFile(Guid id, CancellationToken ct) { await delete.ImportedFileAsync(id, ct); return NoContent(); }

    [HttpDelete("api/organization/sites/{id:guid}"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<IActionResult> Site(Guid id, CancellationToken ct) { await delete.SiteAsync(id, ct); return NoContent(); }

    [HttpDelete("api/organization/departments/{id:guid}"), RequirePermission(Permissions.AdminOrganization)]
    public async Task<IActionResult> Department(Guid id, CancellationToken ct) { await delete.DepartmentAsync(id, ct); return NoContent(); }
}
