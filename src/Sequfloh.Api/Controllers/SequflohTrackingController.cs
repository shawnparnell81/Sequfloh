using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>SEQUFLOH tracking records and tasks: folders, create from a released instruction, data entry, task actions, task events, history.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohTrackingController(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohPartsService parts, SequflohFoundationService f, SequflohPrintService print) : ControllerBase
{
    /// <summary>Folder listing. folder: validation | fai | repair | production | open | completed | on-hold | rejected | all.</summary>
    [HttpGet]
    public Task<object> List([FromQuery] string? folder, [FromQuery] string? q, [FromQuery] TrackingStatus? status, [FromQuery] TrackingType? type, [FromQuery] Guid? owner,
        [FromQuery] Guid? assigned, [FromQuery] string? part, [FromQuery] Guid? department, [FromQuery] Guid? site, [FromQuery] string? dateField, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? priority, [FromQuery] string? result, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        SequflohTrackingQuery.ListAsync(db, m, tracking, f, new TrackingFilter(folder, q, status, type, owner, assigned, part, department, site, dateField, from, to, priority, result, sort, page, pageSize), false, ct);

    /// <summary>Released work instructions a record of this type can execute (the exact revision is stored on the record).</summary>
    [HttpGet("instructions")]
    public async Task<object> Instructions([FromQuery] TrackingType type, [FromQuery] string? q, [FromQuery] Guid? part, CancellationToken ct)
    {
        var types = SequflohTrackingService.InstructionTypes(type);
        var query = (await m.ScopeAsync(db.SequflohWorkInstructions.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.Status == WorkInstructionStatus.Released && types.Contains(x.Type));
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
        var canEnter = x.Status == TrackingStatus.InProgress && await m.CanAsync(Permissions.SequflohStartTask, x.SiteId, x.DepartmentId, ct);
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
                Details = JsonNode.Parse(x.Details), Steps = JsonNode.Parse(x.Steps), Characteristics = JsonNode.Parse(x.Characteristics), PartsRows = JsonNode.Parse(x.PartsRows),
                ElapsedSeconds = active, PausedSeconds = paused, HoldSeconds = hold,
                Owner = Name(x.OwnerId), AssignedUser = Name(x.AssignedUserId), ActiveOperator = Name(x.ActiveOperatorId), CompletedBy = Name(x.CompletedById),
                CreatedBy = Name(x.CreatedById), UpdatedBy = Name(x.UpdatedById),
                Site = db.Sites.Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefault(),
                Department = db.Departments.Where(d => d.Id == x.DepartmentId).Select(d => d.Name).FirstOrDefault()
            },
            Events = events, ServerTime = now, CanEnter = canEnter,
            CanPlan = !SequflohTrackingService.Terminal.Contains(x.Status)
                && (await m.CanAsync(SequflohTrackingService.CreatePermission(x.Type), x.SiteId, x.DepartmentId, ct) || await m.CanAsync(Permissions.SequflohDispatchTask, x.SiteId, x.DepartmentId, ct)),
            CanQualityComment = x.Status is not (TrackingStatus.Completed or TrackingStatus.Canceled) && await m.CanAsync(Permissions.SequflohEditQualityFolder, x.SiteId, x.DepartmentId, ct),
            CanEngineeringComment = x.Status is not (TrackingStatus.Completed or TrackingStatus.Canceled) && await m.CanAsync(Permissions.SequflohEditEngineeringFolder, x.SiteId, x.DepartmentId, ct),
            Actions = await tracking.AvailableActionsAsync(x, ct),
            CompletionProblems = problems, NextAction = SequflohTrackingService.NextAction(x, problems)
        };
    }

    [HttpPost]
    public async Task<object> Create(TrackingCreateInput r, CancellationToken ct) { var x = await tracking.CreateAsync(r, ct); return new { x.Id, x.Number }; }
    [HttpPut("{id:guid}")]
    public async Task<object> Save(Guid id, TrackingSaveInput r, CancellationToken ct) { var x = await tracking.SaveAsync(id, r, ct); return new { x.Id, x.StateVersion, x.Result }; }
    [HttpPost("{id:guid}/actions/{name}")]
    public async Task<object> Act(Guid id, string name, TaskActionInput r, CancellationToken ct) { var x = await tracking.ActAsync(id, name, r, ct); return new { x.Id, x.Status, x.StateVersion }; }
    [HttpGet("{id:guid}/history")]
    public async Task<object> History(Guid id, CancellationToken ct) { await tracking.LoadAsync(id, false, ct); return await parts.HistoryAsync(SequflohTrackingService.EntityType, id, ct); }

    /// <summary>Printable tracking record (PDF from the application's renderer), opened inline in the browser for printing. Audited.</summary>
    [HttpGet("{id:guid}/print")]
    public async Task<IActionResult> Print(Guid id, CancellationToken ct)
    {
        var (pdf, name) = await print.TrackingAsync(id, ct);
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
        return File(pdf, "application/pdf");
    }
}
