using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Imports;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>SEQUFLOH work instructions: designer schema, folders, designer load/save, lifecycle actions, revisions, library copies.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/instructions")]
public sealed class SequflohInstructionsController(AppDbContext db, ModuleSupport m, SequflohInstructionService wi, SequflohPartsService parts, SequflohPrintService print, global::Sequfloh.Platform.IFileStorage storage, SequflohFoundationService f) : ControllerBase
{
    [HttpGet("schema")] public object Schema() => SequflohDesigner.Schemas.Values;

    /// <summary>Folder listing. folder: validation | repair | all | current | drafts | released | superseded | obsolete | type key.</summary>
    [HttpGet]
    public async Task<object> List([FromQuery] string? folder, [FromQuery] string? q, [FromQuery] WorkInstructionStatus? status, [FromQuery] WorkInstructionType? type, [FromQuery] string? part,
        [FromQuery] string? repairCategory, [FromQuery] string? repairType, [FromQuery] string? validationType, [FromQuery] string? resultStatus, [FromQuery] string? sort, [FromQuery] string? tag, [FromQuery] string? developmentLine,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var query = await wi.QueryAsync(ct);
        WorkInstructionType[] validation = [WorkInstructionType.Validation, WorkInstructionType.ValidationFai, WorkInstructionType.Fai];
        WorkInstructionType[] repair = [WorkInstructionType.RepairTest, WorkInstructionType.RepairWork];
        query = (folder ?? "all") switch
        {
            "validation" => query.Where(x => validation.Contains(x.Type)),
            "repair" => query.Where(x => repair.Contains(x.Type)),
            "current" => query.Where(x => x.Status == WorkInstructionStatus.Released),
            "drafts" => query.Where(x => SequflohInstructionService.Editable.Contains(x.Status)),
            "released" => query.Where(x => x.Status == WorkInstructionStatus.Released),
            "validation-folder" => query.Where(x => x.ValidationFolder),
            "released-validation" => query.Where(x => x.ValidationFolder && x.Status == WorkInstructionStatus.Released),
            "superseded" => query.Where(x => x.Status == WorkInstructionStatus.Superseded),
            "obsolete" => query.Where(x => x.Status == WorkInstructionStatus.Obsolete),
            "development" => query.Where(x => x.Status == WorkInstructionStatus.Draft),
            // Validation, Validation / FAI and FAI instructions are one type to the user
            "validation-only" => query.Where(x => (x.Type == WorkInstructionType.Validation || x.Type == WorkInstructionType.ValidationFai || x.Type == WorkInstructionType.Fai)),
            "validation-fai" => query.Where(x => (x.Type == WorkInstructionType.Validation || x.Type == WorkInstructionType.ValidationFai || x.Type == WorkInstructionType.Fai)),
            "fai" => query.Where(x => (x.Type == WorkInstructionType.Validation || x.Type == WorkInstructionType.ValidationFai || x.Type == WorkInstructionType.Fai)),
            "repair-test" => query.Where(x => x.Type == WorkInstructionType.RepairTest),
            "repair-work" => query.Where(x => x.Type == WorkInstructionType.RepairWork),
            "inspection" => query.Where(x => x.Type == WorkInstructionType.Inspection),
            "procedure" => query.Where(x => x.Type == WorkInstructionType.Procedure),
            _ => query
        };
        // instructions imported in the Validation instructions folder stay out of the Work Instructions lists
        if (folder is not ("validation-folder" or "released-validation")) query = query.Where(x => !x.ValidationFolder);
        // instructions of an Engineering, Development product line are listed only in that line
        query = string.IsNullOrWhiteSpace(developmentLine) ? query.Where(x => x.DevelopmentLine == null) : query.Where(x => x.DevelopmentLine == developmentLine);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (type is WorkInstructionType.Validation or WorkInstructionType.ValidationFai or WorkInstructionType.Fai) query = query.Where(x => (x.Type == WorkInstructionType.Validation || x.Type == WorkInstructionType.ValidationFai || x.Type == WorkInstructionType.Fai));
        else if (type is not null) query = query.Where(x => x.Type == type);
        if (!string.IsNullOrWhiteSpace(part)) query = query.Where(x => x.PartNumber != null && EF.Functions.ILike(x.PartNumber, $"%{part.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(repairCategory)) query = query.Where(x => x.RepairCategory == repairCategory);
        if (!string.IsNullOrWhiteSpace(repairType)) query = query.Where(x => x.RepairType == repairType);
        if (!string.IsNullOrWhiteSpace(validationType)) query = query.Where(x => x.ValidationType == validationType);
        if (!string.IsNullOrWhiteSpace(resultStatus)) query = query.Where(x => x.ResultStatus == resultStatus);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.PartNumber ?? "") + " " + (x.RepairDescription ?? ""), like)); }
        query = (sort ?? "updatedAt:desc") switch
        {
            "number:asc" => query.OrderBy(x => x.Number).ThenBy(x => x.Revision), "number:desc" => query.OrderByDescending(x => x.Number).ThenByDescending(x => x.Revision),
            "title:asc" => query.OrderBy(x => x.Title), "title:desc" => query.OrderByDescending(x => x.Title),
            "status:asc" => query.OrderBy(x => x.Status), "status:desc" => query.OrderByDescending(x => x.Status),
            "effectiveDate:asc" => query.OrderBy(x => x.EffectiveDate), "effectiveDate:desc" => query.OrderByDescending(x => x.EffectiveDate),
            "updatedAt:asc" => query.OrderBy(x => x.UpdatedAt), _ => query.OrderByDescending(x => x.UpdatedAt)
        };
        return await Paging.PageAsync(query.Select(x => new
        {
            x.Id, x.Number, x.Title, x.Type, x.Revision, x.Status, x.PartId, x.PartNumber, x.ValidationType, x.FaiType, x.RepairCategory, x.RepairType, x.RepairDescription, x.ResultStatus,
            x.EffectiveDate, x.UpdatedAt, x.Project, x.TargetCompletionDate, x.DevelopmentStatus, x.ReleasedAt,
            AssignedEngineer = db.Users.Where(u => u.Id == x.AssignedEngineerId).Select(u => u.DisplayName).FirstOrDefault(),
            Owner = db.Users.Where(u => u.Id == x.OwnerId).Select(u => u.DisplayName).FirstOrDefault(),
            ReleasedBy = db.Users.Where(u => u.Id == x.ReleasedById).Select(u => u.DisplayName).FirstOrDefault()
        }), page, pageSize, ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<object> Get(Guid id, CancellationToken ct)
    {
        var x = await wi.LoadAsync(id, false, ct);
        string? Name(Guid? u) => u is null ? null : db.Users.Where(y => y.Id == u).Select(y => y.DisplayName).FirstOrDefault();
        var transitions = await db.SequflohWorkInstructionTransitions.AsNoTracking().Where(t => t.WorkInstructionId == id).OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Action, t.FromStatus, t.ToStatus, t.Comment, t.Revision, t.CreatedAt, User = db.Users.Where(u => u.Id == t.UserId).Select(u => u.DisplayName).FirstOrDefault() }).ToListAsync(ct);
        var revisions = await db.SequflohWorkInstructions.AsNoTracking().Where(r => r.FamilyId == x.FamilyId).OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.Revision, r.Status, r.EffectiveDate, r.ReleasedAt, r.SupersededAt, r.RevisionReason, r.CreatedAt }).ToListAsync(ct);
        var canEdit = SequflohInstructionService.Editable.Contains(x.Status) && await m.CanAsync(Permissions.SequflohEditDraftWorkInstruction, x.SiteId, x.DepartmentId, ct);
        return new
        {
            Instruction = new
            {
                x.Id, x.FamilyId, x.Number, x.Type, x.Title, x.Revision, x.Status, x.SiteId, x.DepartmentId, x.OwnerId, x.AuthorId, x.PartId, x.PartNumber, x.PartDescription,
                x.ProductFamily, x.Assembly, x.Model, x.Variant, x.Process, x.Operation, x.Workstation, x.Location, x.ValidationType, x.FaiType, x.RepairCategory, x.RepairType, x.RepairDescription,
                x.SourcePartsListId, x.ResultStatus, x.EffectiveDate, x.SupersededAt, x.ObsoleteAt, x.ReleasedAt, x.RevisionReason, x.CreatedAt, x.UpdatedAt, x.RowVersion,
                x.AssignedEngineerId, x.TargetCompletionDate, x.Project, x.EngineeringNotes, x.DevelopmentStatus,
                Content = JsonNode.Parse(x.Content), Steps = JsonNode.Parse(x.Steps), Characteristics = JsonNode.Parse(x.Characteristics), PartsRows = JsonNode.Parse(x.PartsRows),
                Blocks = JsonNode.Parse(x.Blocks), x.TemplateId, Template = db.SequflohInstructionTemplates.Where(t => t.Id == x.TemplateId).Select(t => t.Name).FirstOrDefault(),
                Owner = Name(x.OwnerId), Author = Name(x.AuthorId),
                ReleasedBy = Name(x.ReleasedById), CreatedBy = Name(x.CreatedById), UpdatedBy = Name(x.UpdatedById),
                Site = db.Sites.Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefault(),
                Department = db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).FirstOrDefault()
            },
            Transitions = transitions, Revisions = revisions, CanEdit = canEdit,
            Actions = await wi.AvailableActionsAsync(x, ct),
            ReleaseProblems = await wi.ReleaseProblemsAsync(x, true, ct),
            CanDevelop = x.Status is not (WorkInstructionStatus.Released or WorkInstructionStatus.Superseded or WorkInstructionStatus.Obsolete)
                && await m.CanAsync(Permissions.SequflohEditEngineeringFolder, x.SiteId, x.DepartmentId, ct),
            CanDuplicate = await m.CanAsync(Permissions.SequflohCreateWorkInstruction, x.SiteId, x.DepartmentId, ct)
        };
    }

    [HttpPost] [RequirePermission(Permissions.SequflohCreateWorkInstruction)]
    public async Task<object> Create(InstructionCreateInput r, CancellationToken ct) { var x = await wi.CreateAsync(r, ct); return new { x.Id, x.Number }; }

    /// <summary>Imports one file as a new draft work instruction: the title is the file name and the file is attached to the draft (same file rules as attachments).</summary>
    [HttpPost("import"), RequirePermission(Permissions.SequflohCreateWorkInstruction), RequestSizeLimit(global::Sequfloh.Platform.FilesController.MaxBytes + 64 * 1024)]
    public async Task<object> Import(IFormFile file, [FromForm] WorkInstructionType type, [FromForm] Guid siteId, [FromForm] Guid? departmentId, CancellationToken ct, [FromForm] string? number = null, [FromForm] bool validationFolder = false, [FromForm] string? developmentLine = null)
    {
        global::Sequfloh.Platform.FilesController.Check(file);
        developmentLine = string.IsNullOrWhiteSpace(developmentLine) ? null : developmentLine.Trim();
        if (developmentLine is not null && !DevelopmentLines.All.ContainsKey(developmentLine)) throw new AppException("Choose a development product line.");
        // the number and title are the file name as it was saved, without its extension
        var title = Path.GetFileNameWithoutExtension(Path.GetFileName(file.FileName)).Trim();
        if (title.Length > 200) title = title[..200];
        // a Procedure Instruction imported again under the same title is revised: a released one gets a new draft revision, which then carries the new file
        if (type == WorkInstructionType.Procedure && await wi.ProcedureToReviseAsync(title, siteId, ct, developmentLine) is { } current)
        {
            var target = current.Status == WorkInstructionStatus.Released ? await wi.ReviseAsync(current.Id, new ReviseInput("New file imported"), ct) : current;
            await wi.ArchiveFilesAsync(target.Id, ct);
            await global::Sequfloh.Platform.FilesController.AttachAsync(db, storage, "SequflohWorkInstruction", target.Id, target.SiteId, file, ct);
            return new { target.Id, target.Number, Title = title, Revised = true };
        }
        // the file was checked above; creating the draft runs in its own transaction, so a failed attach takes the new draft away again
        var x = await wi.CreateAsync(new InstructionCreateInput(type, title, siteId, departmentId, null, Number: string.IsNullOrWhiteSpace(number) ? title : number, DevelopmentLine: developmentLine), ct);
        try { await global::Sequfloh.Platform.FilesController.AttachAsync(db, storage, "SequflohWorkInstruction", x.Id, x.SiteId, file, ct); }
        catch
        {
            db.ChangeTracker.Clear();
            var made = await db.SequflohWorkInstructions.FindAsync([x.Id], CancellationToken.None);
            if (made is not null) { db.SequflohWorkInstructions.Remove(made); await db.SaveChangesAsync(CancellationToken.None); }
            throw;
        }
        if (developmentLine is not null)
        {
            f.Audit("WorkInstructionImportedToDevelopment", "SequflohWorkInstruction", x.Id, $"{x.Number} rev {x.Revision} imported from {file.FileName} into {DevelopmentLines.All[developmentLine]}", x.SiteId);
            await db.SaveChangesAsync(ct);
        }
        else if (validationFolder)
        {
            x.ValidationFolder = true;
            f.Audit("WorkInstructionImportedToValidation", "SequflohWorkInstruction", x.Id, $"{x.Number} rev {x.Revision} imported from {file.FileName} into Engineering Validation instructions", x.SiteId);
            await db.SaveChangesAsync(ct);
        }
        return new { x.Id, x.Number, Title = title };
    }
    public sealed record InstructionTypeInput(WorkInstructionType Type);
    /// <summary>Changes the instruction type (any type to any type); the change is recorded in the audit trail with the old and new type.</summary>
    [HttpPut("{id:guid}/type")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<IActionResult> ChangeType(Guid id, InstructionTypeInput r, CancellationToken ct) { await wi.ChangeTypeAsync(id, r.Type, ct); return NoContent(); }
    [HttpPut("{id:guid}")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<IActionResult> Save(Guid id, InstructionSaveInput r, CancellationToken ct) { await wi.SaveAsync(id, r, ct); return NoContent(); }
    [HttpPut("{id:guid}/development")] [RequirePermission(Permissions.SequflohEditEngineeringFolder)]
    public async Task<IActionResult> Development(Guid id, DevelopmentInput r, CancellationToken ct) { await wi.SaveDevelopmentAsync(id, r, ct); return NoContent(); }
    [HttpPost("{id:guid}/actions/{name}")]
    public async Task<object> Act(Guid id, string name, InstructionActionInput r, CancellationToken ct) { var x = await wi.ActAsync(id, name, r, ct); return new { x.Id, x.Status }; }
    [HttpPost("{id:guid}/revise")] [RequirePermission(Permissions.SequflohReviseWorkInstruction)]
    public async Task<object> Revise(Guid id, ReviseInput r, CancellationToken ct) { var x = await wi.ReviseAsync(id, r, ct); return new { x.Id, x.Number, x.Revision }; }
    [HttpPost("{id:guid}/duplicate")] [RequirePermission(Permissions.SequflohCreateWorkInstruction)]
    public async Task<object> Duplicate(Guid id, RecordNumberInput r, CancellationToken ct) { var x = await wi.DuplicateAsync(id, r.Number, ct); return new { x.Id, x.Number }; }
    [HttpPost("{id:guid}/copy-operation-steps")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<object> CopySteps(Guid id, CopyStepsInput r, CancellationToken ct) => new { Copied = await wi.CopyOperationStepsAsync(id, r, ct) };
    [HttpPost("{id:guid}/copy-parts")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<object> CopyParts(Guid id, CopyPartsListInput r, CancellationToken ct) => new { Copied = await wi.CopyPartsAsync(id, r, ct) };
    [HttpPost("{id:guid}/copy-characteristics")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<object> CopyCharacteristics(Guid id, CopyCharacteristicsInput r, CancellationToken ct) => new { Copied = await wi.CopyCharacteristicsAsync(id, r, ct) };
    [HttpPost("{id:guid}/apply-template")] [RequirePermission(Permissions.SequflohEditDraftWorkInstruction)]
    public async Task<object> ApplyTemplate(Guid id, ApplyTemplateInput r, CancellationToken ct) { var x = await wi.ApplyTemplateAsync(id, r, ct); return new { x.Id, Blocks = JsonNode.Parse(x.Blocks) }; }
    [HttpPost("{id:guid}/save-as-template")] [RequirePermission(Permissions.SequflohCreateWorkInstruction)]
    public async Task<object> SaveAsTemplate(Guid id, SaveAsTemplateInput r, [FromServices] SequflohTemplateService templates, CancellationToken ct)
    { var t = await templates.SaveFromInstructionAsync(id, r, wi, ct); return new { t.Id, t.Name }; }
    /// <summary>Controlled printable layout (PDF from the application's renderer), opened inline in the browser for printing.</summary>
    [HttpGet("{id:guid}/print")]
    public async Task<IActionResult> Print(Guid id, CancellationToken ct)
    {
        var (pdf, name) = await print.InstructionAsync(id, ct);
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
        return File(pdf, "application/pdf");
    }
    [HttpGet("{id:guid}/history")] [RequirePermission(Permissions.SequflohViewAuditTrail)]
    public async Task<object> History(Guid id, CancellationToken ct) { await wi.LoadAsync(id, false, ct); return await parts.HistoryAsync(SequflohInstructionService.EntityType, id, ct); }
}
