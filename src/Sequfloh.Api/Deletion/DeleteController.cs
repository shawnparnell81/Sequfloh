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

    [HttpPost("api/sequfloh/tracking/{id:guid}/delete-permanently"), RequirePermission(Permissions.SequflohDeletePermanently)]
    public async Task<IActionResult> WorkOrderPermanently(Guid id, PermanentDeleteInput x, CancellationToken ct) { await delete.WorkOrderPermanentlyAsync(id, x.ConfirmNumber, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/tracking/job/test-files/{fileId:guid}/permanently"), RequirePermission(Permissions.SequflohDeletePermanently)]
    public async Task<IActionResult> RemovedFilePermanently(Guid fileId, CancellationToken ct) { await delete.RemovedFilePermanentlyAsync(fileId, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/inspection-requests/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> InspectionRequest(Guid id, CancellationToken ct) { await delete.InspectionRequestAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/parts/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> Part(Guid id, CancellationToken ct) { await delete.PartAsync(id, ct); return NoContent(); }

    /// <summary>How many parts in one product family can be deleted, and which stay because work orders, instructions or parts lists use them.</summary>
    [HttpGet("api/sequfloh/parts/families/{family}/delete-preview"), RequirePermission(P)]
    public Task<FamilyPartsDeletePlan> FamilyPartsPreview(string family, CancellationToken ct) => delete.PreviewFamilyPartsAsync(family, ct);

    /// <summary>Deletes the unused parts of one product family. Parts tied to work orders, instructions or parts lists stay. Repeat the call while More is true.</summary>
    [HttpPost("api/sequfloh/parts/families/{family}/delete"), RequirePermission(P)]
    public Task<FamilyPartsDeleteResult> FamilyParts(string family, CancellationToken ct) => delete.DeleteFamilyPartsAsync(family, ct);

    [HttpDelete("api/sequfloh/parts-lists/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> PartsList(Guid id, CancellationToken ct) { await delete.PartsListAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/operation-steps/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> OperationStep(Guid id, CancellationToken ct) { await delete.OperationStepAsync(id, ct); return NoContent(); }

    [HttpDelete("api/sequfloh/instruction-templates/{id:guid}"), RequirePermission(P)]
    public async Task<IActionResult> Template(Guid id, CancellationToken ct) { await delete.TemplateAsync(id, ct); return NoContent(); }

    /// <summary>How many drawings in one family can be deleted, and which stay because deleting that drawing would be refused.</summary>
    [HttpGet("api/drawings/families/{family}/delete-preview"), RequirePermission(P)]
    public Task<FamilyDrawingsDeletePlan> FamilyDrawingsPreview(string family, CancellationToken ct) => delete.PreviewFamilyDrawingsAsync(family, ct);

    /// <summary>Deletes the drawings of one family. A drawing that cannot be deleted stays, with the reason. Repeat the call while More is true.</summary>
    [HttpPost("api/drawings/families/{family}/delete"), RequirePermission(P)]
    public Task<FamilyDrawingsDeleteResult> FamilyDrawings(string family, CancellationToken ct) => delete.DeleteFamilyDrawingsAsync(family, ct);

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

public sealed record PermanentDeleteInput(string? ConfirmNumber);
