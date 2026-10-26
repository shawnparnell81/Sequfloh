using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>SEQUFLOH tracking records and tasks: folders, create from a released instruction, data entry, task actions, task events, history.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohTrackingController(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohPartsService parts, SequflohFoundationService f, SequflohPrintService print, global::Sequfloh.Imports.ImportedFileService files) : ControllerBase
{
    /// <summary>Folder listing. folder: validation | fai | repair | production | open | completed | on-hold | rejected | all.</summary>
    [HttpGet]
    public Task<object> List([FromQuery] string? folder, [FromQuery] string? q, [FromQuery] TrackingStatus? status, [FromQuery] TrackingType? type, [FromQuery] Guid? owner,
        [FromQuery] Guid? assigned, [FromQuery] string? part, [FromQuery] Guid? department, [FromQuery] Guid? site, [FromQuery] string? dateField, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? priority, [FromQuery] string? result, [FromQuery] string? sort, [FromQuery] string? tag, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        SequflohTrackingQuery.ListAsync(db, m, tracking, f, new TrackingFilter(folder, q, status, type, owner, assigned, part, department, site, dateField, from, to, priority, result, sort, page, pageSize, tag), false, ct);

    /// <summary>Released work instructions a record of this type can execute (the exact revision is stored on the record).</summary>
    /// <summary>The suggested automatic number for a new work order, or null when the type or the family has no code.</summary>
    [HttpGet("next-number")]
    public async Task<object> NextNumber([FromQuery] TrackingType type, [FromQuery] Guid? familyId, [FromQuery] Guid? partId, [FromQuery] Guid siteId, CancellationToken ct)
    {
        var prefix = await tracking.NumberPrefixAsync(type, familyId, partId, siteId, ct);
        return new { Number = prefix is null ? null : await tracking.NextNumberAsync(prefix, false, ct) };
    }
    [HttpGet("instructions")]
    public async Task<object> Instructions([FromQuery] TrackingType type, [FromQuery] string? q, [FromQuery] Guid? part, CancellationToken ct, [FromQuery] string? line = null)
    {
        var types = SequflohTrackingService.InstructionTypes(type);
        var devLine = type == TrackingType.Development && !string.IsNullOrWhiteSpace(line) ? line.Trim() : null;
        // instructions of a development line are offered only on Development work orders of that line
        var query = (await m.ScopeAsync(db.SequflohWorkInstructions.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.Status == WorkInstructionStatus.Released
            && (x.DevelopmentLine == null ? types.Contains(x.Type) : x.DevelopmentLine == devLine));
        if (part is not null) query = query.Where(x => x.PartId == part || x.PartId == null);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.PartNumber ?? ""), like)); }
        return await query.OrderBy(x => x.Number).Take(50).Select(x => new { x.Id, x.Number, x.Revision, x.Title, x.Type, x.PartId, x.PartNumber, x.PartDescription, x.Operation, x.RepairDescription, x.SiteId, x.DepartmentId }).ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<object> Get(Guid id, CancellationToken ct)
    {
        var x = await tracking.LoadAsync(id, false, ct);
        var now = await tracking.DbNowAsync(ct);
        var (active, paused, hold) = SequflohTrackingService.Durations(x, now);
        string? Name(Guid? u) => u is null ? null : db.Users.Where(y => y.Id == u).Select(y => y.DisplayName).FirstOrDefault();
        var events = await db.SequflohTaskEvents.AsNoTracking().Where(e => e.TrackingRecordId == id).OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Id, e.EventType, e.FromStatus, e.ToStatus, e.CreatedAt, e.UserName, e.Reason, e.Comment, e.ActiveSeconds, e.PausedSeconds, e.HoldSeconds, e.OperationId,
                AssignedBefore = db.Users.Where(u => u.Id == e.AssignedBeforeId).Select(u => u.DisplayName).FirstOrDefault(),
                AssignedAfter = db.Users.Where(u => u.Id == e.AssignedAfterId).Select(u => u.DisplayName).FirstOrDefault() }).ToListAsync(ct);
        var problems = await tracking.CompletionProblemsAsync(x, ct);
        var canEnter = x.Status == TrackingStatus.InProgress;
        return new
        {
            Record = new
            {
                x.Id, x.Number, x.Type, x.WorkInstructionId, x.WorkInstructionNumber, x.WorkInstructionRevision, x.WorkInstructionTitle, x.PartId, x.PartNumber, x.PartDescription, x.SerialNumber, x.LotNumber,
                x.WorkOrder, x.RepairOrder, x.CustomerReturnReference, x.Project, x.Operation, x.OwnerId, x.AssignedUserId, x.DepartmentId, x.SiteId, x.Status, x.Priority, x.PlannedStart, x.DueAt,
                x.ActualStart, x.ActualCompletion, x.CurrentStateStartedAt, x.StateVersion, x.LastTransitionId, x.LastTransitionAt, x.MinBenchmarkMinutes, x.MaxBenchmarkMinutes, x.VarianceMinutes,
                x.VarianceReason, x.Result, x.EngineeringConclusion,
                x.EvidenceReferences, x.TechnicianComments, x.QualityComments, x.EngineeringComments, x.Deviations, x.NonconformanceReferences, x.PauseReason, x.HoldReason, x.CancelReason,
                x.FailureReason, x.ReportedCondition, x.ObservedCondition, x.RepairDescription, x.FinalDisposition, x.FaiType, x.CreatedAt, x.UpdatedAt,
                x.QuantityTarget, x.DevelopmentLine, x.InspectionSubType, x.InspectionDescription, x.QtyGood, x.QtyBad, x.FurtherInspection, x.Quarantine, x.QuarantineReason,
                Details = JsonNode.Parse(x.Details), Steps = JsonNode.Parse(x.Steps), Characteristics = JsonNode.Parse(x.Characteristics), PartsRows = JsonNode.Parse(x.PartsRows),
                ElapsedSeconds = active, PausedSeconds = paused, HoldSeconds = hold,
                Owner = Name(x.OwnerId), AssignedUser = Name(x.AssignedUserId),
                Assignees = db.SequflohTaskAssignees.Where(a => a.TrackingRecordId == x.Id).OrderBy(a => a.AddedAt).Join(db.Users, a => a.UserId, u => u.Id, (a, u) => new { u.Id, u.DisplayName }).ToList(), ActiveOperator = Name(x.ActiveOperatorId), CompletedBy = Name(x.CompletedById),
                CreatedBy = Name(x.CreatedById), UpdatedBy = Name(x.UpdatedById),
                x.ProjectId, ProjectLabel = db.SequflohProjects.Where(p => p.Id == x.ProjectId).Select(p => p.Number + " " + p.Name).FirstOrDefault(),
                Site = db.Sites.Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefault(),
                Department = db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).FirstOrDefault()
            },
            Events = events, ServerTime = now, CanEnter = canEnter,
            CanAssign = !SequflohTrackingService.Terminal.Contains(x.Status)
                && (await m.CanAsync(Permissions.SequflohDispatchTask, x.SiteId, x.DepartmentId, ct) || await m.CanAsync(Permissions.SequflohReassignTask, x.SiteId, x.DepartmentId, ct)),
            CanDelete = await m.CanAsync(Permissions.SequflohDeleteRecords, x.SiteId, x.DepartmentId, ct) && !global::Sequfloh.Deletion.DeletionService.HasTime(x)
                && !await db.SequflohTimeEntries.AnyAsync(e => e.TrackingRecordId == x.Id, ct),
            CanPlan = !SequflohTrackingService.Terminal.Contains(x.Status)
                && (await m.CanAsync(SequflohTrackingService.CreatePermission(x.Type), x.SiteId, x.DepartmentId, ct) || await m.CanAsync(Permissions.SequflohDispatchTask, x.SiteId, x.DepartmentId, ct)),
            CanAttachForm = CanAttachShape(x) && await m.CanAsync(SequflohTrackingService.CreatePermission(x.Type), x.SiteId, x.DepartmentId, ct),
            CanQualityComment = x.Status is not (TrackingStatus.Completed or TrackingStatus.Canceled) && await m.CanAsync(Permissions.SequflohEditQualityFolder, x.SiteId, x.DepartmentId, ct),
            CanEngineeringComment = x.Status is not (TrackingStatus.Completed or TrackingStatus.Canceled) && await m.CanAsync(Permissions.SequflohEditEngineeringFolder, x.SiteId, x.DepartmentId, ct),
            Actions = await tracking.AvailableActionsAsync(x, ct),
            CompletionProblems = problems, NextAction = SequflohTrackingService.NextAction(x, problems)
        };
    }

    [HttpPost]
    public async Task<object> Create(TrackingCreateInput r, CancellationToken ct)
    {
        var x = await tracking.CreateAsync(r, ct);
        object? form = null;
        var formNumber = (r.FormNumber ?? "").Trim();
        var typed = formNumber.Length > 0 && r.FamilyId is not null;
        if (typed || (x.Type == TrackingType.Validation && r.FamilyId is not null) || x.Type == TrackingType.SaltSpray)
        {
            // nothing is numbered for the user: without a typed number the form waits for Attach validation form
            var info = await StartFormAsync(x.Type, x.Number, formNumber.Length > 0 ? formNumber : null, x.PartNumber, r.FamilyId, x.Id, ct);
            await tracking.SetValidationFormAsync(x.Id, info, ct);
            form = info;
        }
        return new { x.Id, x.Number, ValidationForm = form };
    }
    /// <summary>Starts the confirmed form copy for a product family (validation) or Salt Spray Test. The title carries the work order number; the copy is numbered exactly as the user typed it and is linked to the work order. Returns what was attached, or why nothing was.</summary>
    async Task<JsonObject> StartFormAsync(TrackingType type, string number, string? copyNumber, string? partNumber, Guid? fam, Guid workOrderId, CancellationToken ct)
    {
        if (type == TrackingType.SaltSpray && fam is null)
        {
            // only the form an administrator confirmed for Salt Spray Test is attached, never a suggestion
            var info = new JsonObject { ["test"] = "Salt Spray Test" };
            var blankId = (await db.SequflohSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.SaltSprayFormId;
            if (blankId is { } bid && await db.ImportedFiles.AnyAsync(b => b.Id == bid && b.IsBlankForm && !b.IsArchived, ct))
            {
                if (string.IsNullOrWhiteSpace(copyNumber)) { info["message"] = Untyped; return info; }
                try { Copy(info, await files.FillOutAsync(bid, copyNumber, $"Salt Spray Test {number}", ct, partNumber, fam, workOrderId)); }
                catch (Exception e) when (e is AppException) { info["message"] = $"The Salt Spray Test form could not be started: {e.Message}"; }
            }
            else info["message"] = "No form is confirmed for Salt Spray Test. An administrator can set one in Settings > Validation forms by product.";
            return info;
        }
        {
            // only a form the administrator confirmed for the family is attached; nothing is guessed here
            var family = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == fam && v.ListKey == SequflohCatalog.ProductFamilies, ct);
            var info = new JsonObject { ["familyId"] = fam?.ToString(), ["family"] = family?.Label };
            if (family?.ValidationFormId is { } blankId && await db.ImportedFiles.AnyAsync(b => b.Id == blankId && b.IsBlankForm && !b.IsArchived, ct))
            {
                if (string.IsNullOrWhiteSpace(copyNumber)) { info["message"] = Untyped; return info; }
                try { Copy(info, await files.FillOutAsync(blankId, copyNumber, $"{family.Label} validation {number}", ct, partNumber, fam, workOrderId)); }
                catch (Exception e) when (e is AppException) { info["message"] = $"The validation form for {family.Label} could not be started: {e.Message}"; }
            }
            else info["message"] = $"No validation form is confirmed for {family?.Label ?? "this product family"}. An administrator can set one in Settings > Validation forms by product.";
            return info;
        }
    }
    const string Untyped = "Type the number and file name of the form, then use Attach validation form.";
    static void Copy(JsonObject info, object started)
    {
        var copy = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(started))!.AsObject();
        info["fileId"] = copy["Id"]?.ToString(); info["fileNumber"] = copy["Number"]?.ToString(); info["fileTitle"] = copy["Title"]?.ToString();
    }
    static Guid? FormFamily(SequflohTrackingRecord x)
    {
        var v = JsonNode.Parse(x.Details)?["validationForm"]?["familyId"]?.ToString();
        return Guid.TryParse(v, out var g) ? g : null;
    }
    static bool HasForm(SequflohTrackingRecord x) => !string.IsNullOrEmpty(JsonNode.Parse(x.Details)?["validationForm"]?["fileId"]?.ToString());
    bool CanAttachShape(SequflohTrackingRecord x) => x.Status is not (TrackingStatus.Completed or TrackingStatus.Canceled) && !HasForm(x);
    /// <summary>The typed number and file name of the copy, and the product family whose validation form is used (the work order's own family when left out).</summary>
    public sealed record AttachFormInput(string? Number, Guid? FamilyId = null);
    /// <summary>Starts the confirmed form copy on a work order that has none yet, for example when the copy at create failed because the number was taken.</summary>
    [HttpPost("{id:guid}/validation-form")]
    public async Task<object> AttachForm(Guid id, AttachFormInput r, CancellationToken ct)
    {
        var x = await db.SequflohTrackingRecords.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("The work order");
        await m.RequireAsync(SequflohTrackingService.CreatePermission(x.Type), x.SiteId, x.DepartmentId, ct);
        SequflohTrackingService.RequireOpen(x);
        if (HasForm(x)) throw new ConflictException($"{x.Number} already has a form attached.");
        var number = (r.Number ?? "").Trim();
        if (number.Length == 0) throw new AppException("Type the number and file name of the form.");
        var fam = r.FamilyId ?? FormFamily(x);
        if (fam is null && x.Type != TrackingType.SaltSpray) throw new AppException("Choose the product family whose validation form to attach.");
        var info = await StartFormAsync(x.Type, x.Number, number, x.PartNumber, fam, x.Id, ct);
        if (info["fileId"] is null) throw new AppException(info["message"]?.ToString() ?? "The form could not be started.");
        f.Audit("ValidationFormAttached", SequflohTrackingService.EntityType, x.Id, $"{x.Number} form {info["fileNumber"]} attached", x.SiteId, context: new { fileId = info["fileId"]?.ToString(), number });
        await tracking.SetValidationFormAsync(x.Id, info, ct);
        return info;
    }
    /// <summary>Records qty good and bad, further inspection and the quarantine flag of an inspection or warranty work order.</summary>
    [HttpPut("{id:guid}/inspection-results")]
    public async Task<object> InspectionResults(Guid id, InspectionResultsInput r, CancellationToken ct) { var x = await tracking.SetInspectionResultsAsync(id, r, ct); return new { x.Id, x.QtyGood, x.QtyBad, x.FurtherInspection, x.Quarantine, x.QuarantineReason }; }
    public sealed record InstructionRefInput(Guid? WorkInstructionId);
    /// <summary>Attaches, replaces or removes the optional work instruction of a work order before work starts.</summary>
    [HttpPut("{id:guid}/instruction")]
    public async Task<object> SetInstruction(Guid id, InstructionRefInput r, CancellationToken ct) { var x = await tracking.SetInstructionAsync(id, r.WorkInstructionId, ct); return new { x.Id, x.WorkInstructionId }; }
    [HttpPut("{id:guid}")]
    public async Task<object> Save(Guid id, TrackingSaveInput r, CancellationToken ct) { var x = await tracking.SaveAsync(id, r, ct); return new { x.Id, x.StateVersion, x.Result }; }
    [HttpPost("{id:guid}/actions/{name}")]
    public async Task<object> Act(Guid id, string name, TaskActionInput r, CancellationToken ct, [FromServices] SequflohJobService? jobs = null)
    {
        var was = await db.SequflohTrackingRecords.AsNoTracking().Where(e => e.Id == id).Select(e => (TrackingStatus?)e.Status).FirstOrDefaultAsync(ct);
        var x = await tracking.ActAsync(id, name, r, ct);
        // completing a work order saves its job report to Reports; a reopened order completed again gets a new one
        if (jobs is not null && was != TrackingStatus.Completed && x.Status == TrackingStatus.Completed) await jobs.SaveReportAsync(x.Id, "Completed", print, ct);
        return new { x.Id, x.Status, x.StateVersion };
    }
    [HttpGet("{id:guid}/history")] [RequirePermission(Permissions.SequflohViewAuditTrail)]
    public async Task<object> History(Guid id, CancellationToken ct)
    {
        await tracking.LoadAsync(id, false, ct);
        // the work order's own entries plus its timers and test files
        var related = await db.SequflohTimeEntries.Where(e => e.TrackingRecordId == id).Select(e => e.Id)
            .Concat(db.SequflohTestFiles.Where(e => e.TrackingRecordId == id).Select(e => e.Id)).ToListAsync(ct);
        return await db.AuditEntries.AsNoTracking().Where(a => (a.EntityType == SequflohTrackingService.EntityType && a.EntityId == id) || related.Contains(a.EntityId))
            .OrderByDescending(a => a.OccurredAt).Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Changes, a.Reason }).Take(1000).ToListAsync(ct);
    }

    /// <summary>Printable tracking record (PDF from the application's renderer), opened inline in the browser for printing. Audited.</summary>
    [HttpGet("{id:guid}/print")]
    public async Task<IActionResult> Print(Guid id, CancellationToken ct)
    {
        var (pdf, name) = await print.TrackingAsync(id, ct);
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
        return File(pdf, "application/pdf");
    }
}
