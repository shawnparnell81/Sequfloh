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

/// <summary>
/// SEQUFLOH Home, Quality and Engineering dashboards and review queues. Every figure is a count or list of stored records the user can see
/// (site and department scope); sections the user has no permission for are left out.
/// </summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh")]
public sealed class SequflohDashboardController(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohInstructionService instructions, SequflohFoundationService f) : ControllerBase
{
    static readonly WorkInstructionType[] ValidationTypes = [WorkInstructionType.Validation, WorkInstructionType.ValidationFai];
    static readonly WorkInstructionType[] RepairTypes = [WorkInstructionType.RepairTest, WorkInstructionType.RepairWork];

    async Task<object> Recent(IQueryable<SequflohWorkInstruction> wis, bool revised, CancellationToken ct) =>
        await wis.Where(x => x.Status == WorkInstructionStatus.Released && x.ReleasedAt != null && (!revised || x.PreviousRevisionId != null)).OrderByDescending(x => x.ReleasedAt).Take(5)
            .Select(x => new { x.Id, x.Number, x.Revision, x.Title, x.Type, x.ReleasedAt, x.RevisionReason }).ToListAsync(ct);

    static IQueryable<SequflohTrackingRecord> AwaitingQuality(IQueryable<SequflohTrackingRecord> q) => q.Where(x => x.Status == TrackingStatus.PendingReview && x.QualityReviewRequired && x.QualityReviewedAt == null);
    static IQueryable<SequflohTrackingRecord> AwaitingEngineering(IQueryable<SequflohTrackingRecord> q) =>
        q.Where(x => x.Status == TrackingStatus.PendingReview && x.EngineeringReviewRequired && x.EngineeringReviewedAt == null && (!x.QualityReviewRequired || x.QualityReviewedAt != null));
    static IQueryable<SequflohTrackingRecord> Failed(IQueryable<SequflohTrackingRecord> q, TrackingType t) => q.Where(x => x.Type == t && (x.Status == TrackingStatus.Failed || x.Result == "Fail"));
    static async Task<object> Rows(IQueryable<SequflohTrackingRecord> q, CancellationToken ct) => await q.OrderByDescending(x => x.UpdatedAt).Take(5)
        .Select(x => new { x.Id, x.Number, x.Type, x.Status, x.Result, x.PartNumber, x.WorkInstructionNumber, x.WorkInstructionRevision, x.DueAt, x.UpdatedAt }).ToListAsync(ct);
    static async Task<object> WiRows(IQueryable<SequflohWorkInstruction> q, CancellationToken ct) => await q.OrderByDescending(x => x.UpdatedAt).Take(5).Select(x => new { x.Id, x.Number, x.Revision, x.Title, x.Type, x.Status, x.UpdatedAt }).ToListAsync(ct);

    /// <summary>SEQUFLOH Home dashboard.</summary>
    [HttpGet("dashboard")] [RequirePermission(Permissions.SequflohViewDashboard)]
    public async Task<object> Home(CancellationToken ct)
    {
        var tr = await tracking.QueryAsync(ct);
        var wis = await instructions.QueryAsync(ct);
        var now = await tracking.DbNowAsync(ct);
        var cutoff = now.AddHours(-(await f.GetSettingsAsync(ct)).Tracking.OverdueThresholdHours);
        var me = m.UserId;
        var live = await m.CanAnyAsync(Permissions.SequflohViewLiveExecution, ct);
        var open = tr.Where(x => SequflohTrackingService.Open.Contains(x.Status));
        var taskEvents = await db.SequflohTaskEvents.AsNoTracking().Where(e => tr.Any(r => r.Id == e.TrackingRecordId)).OrderByDescending(e => e.CreatedAt).Take(10)
            .Select(e => new { Id = e.TrackingRecordId, At = e.CreatedAt, Number = e.TrackingNumber, Kind = "Tracking", Text = e.EventType, User = e.UserName }).ToListAsync(ct);
        var wiEvents = await db.SequflohWorkInstructionTransitions.AsNoTracking().Where(t => wis.Any(w => w.Id == t.WorkInstructionId)).OrderByDescending(t => t.CreatedAt).Take(10)
            .Select(t => new { Id = t.WorkInstructionId, At = t.CreatedAt, Number = db.SequflohWorkInstructions.Where(w => w.Id == t.WorkInstructionId).Select(w => w.Number + " rev " + w.Revision).FirstOrDefault() ?? "",
                Kind = "Work instruction", Text = t.Action, User = db.Users.Where(u => u.Id == t.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? "" }).ToListAsync(ct);
        return new
        {
            Live = live ? new
            {
                AwaitingDispatch = await tr.CountAsync(x => x.Status == TrackingStatus.NotStarted || x.Status == TrackingStatus.Assigned, ct),
                Active = await tr.CountAsync(x => x.Status == TrackingStatus.InProgress || x.Status == TrackingStatus.PendingInspection, ct),
                Paused = await tr.CountAsync(x => x.Status == TrackingStatus.Paused, ct),
                Overdue = await open.CountAsync(x => x.DueAt != null && x.DueAt < cutoff, ct),
                ValidationInProgress = await open.CountAsync(x => x.Type == TrackingType.Validation, ct),
                FaiInProgress = await open.CountAsync(x => x.Type == TrackingType.Fai, ct),
                RepairInProgress = await open.CountAsync(x => x.Type == TrackingType.Repair, ct),
                ProductionInProgress = await open.CountAsync(x => x.Type == TrackingType.Production, ct),
                Failed = await tr.CountAsync(x => x.Status == TrackingStatus.Failed, ct),
                AssignedToMe = await open.CountAsync(x => x.AssignedUserId == me, ct),
                MyWork = await Rows(open.Where(x => x.AssignedUserId == me), ct),
            } : null,
            Instructions = new
            {
                PendingReview = await wis.CountAsync(x => x.Status == WorkInstructionStatus.PendingReview, ct) + await tr.CountAsync(x => x.Status == TrackingStatus.PendingReview, ct),
                PendingApproval = await wis.CountAsync(x => x.Status == WorkInstructionStatus.PendingApproval, ct),
                RecentlyReleased = await Recent(wis, false, ct),
                RecentlyRevised = await Recent(wis, true, ct),
            },
            RecentActivity = taskEvents.Concat(wiEvents).OrderByDescending(e => e.At).Take(10),
            ServerTime = now
        };
    }

    /// <summary>Quality Dashboard.</summary>
    [HttpGet("quality/dashboard")] [RequirePermission(Permissions.SequflohViewQualityFolder)]
    public async Task<object> Quality(CancellationToken ct)
    {
        var tr = await tracking.QueryAsync(ct);
        var wis = await instructions.QueryAsync(ct);
        var now = await tracking.DbNowAsync(ct);
        var cutoff = now.AddHours(-(await f.GetSettingsAsync(ct)).Tracking.OverdueThresholdHours);
        var overdue = tr.Where(x => (x.Status == TrackingStatus.PendingInspection || (x.Status == TrackingStatus.PendingReview && x.QualityReviewRequired && x.QualityReviewedAt == null)) && x.DueAt != null && x.DueAt < cutoff);
        return new
        {
            ValidationPendingReview = await wis.CountAsync(x => ValidationTypes.Contains(x.Type) && x.Status == WorkInstructionStatus.PendingReview, ct),
            RepairPendingReview = await wis.CountAsync(x => RepairTypes.Contains(x.Type) && x.Status == WorkInstructionStatus.PendingReview, ct),
            ApprovalsAwaitingQuality = await wis.CountAsync(x => x.Status == WorkInstructionStatus.PendingApproval, ct) + await AwaitingQuality(tr).CountAsync(ct),
            FailedValidations = await Failed(tr, TrackingType.Validation).CountAsync(ct),
            FailedFai = await Failed(tr, TrackingType.Fai).CountAsync(ct),
            FailedRepairTests = await Failed(tr, TrackingType.Repair).CountAsync(ct),
            OverdueQualityActions = await overdue.CountAsync(ct),
            FailedRecords = await Rows(tr.Where(x => x.Status == TrackingStatus.Failed || x.Result == "Fail"), ct),
            OverdueRows = await Rows(overdue, ct),
            RecentlyReleased = await Recent(wis, false, ct),
            RecentlyRevised = await Recent(wis, true, ct),
        };
    }

    /// <summary>Engineering Dashboard.</summary>
    [HttpGet("engineering/dashboard")] [RequirePermission(Permissions.SequflohViewEngineeringFolder)]
    public async Task<object> Engineering(CancellationToken ct)
    {
        var tr = await tracking.QueryAsync(ct);
        var wis = await instructions.QueryAsync(ct);
        var dev = wis.Where(x => x.Status == WorkInstructionStatus.Draft || x.Status == WorkInstructionStatus.InDevelopment || x.Status == WorkInstructionStatus.ChangesRequested);
        return new
        {
            InDevelopment = await dev.CountAsync(ct),
            DraftValidation = await dev.CountAsync(x => ValidationTypes.Contains(x.Type), ct),
            DraftRepair = await dev.CountAsync(x => RepairTypes.Contains(x.Type), ct),
            ChangesRequested = await wis.CountAsync(x => x.Status == WorkInstructionStatus.ChangesRequested, ct),
            RecommendedForRelease = await wis.CountAsync(x => x.ReleaseRecommendedAt != null && x.Status != WorkInstructionStatus.Released && x.Status != WorkInstructionStatus.Superseded
                && x.Status != WorkInstructionStatus.Obsolete && x.Status != WorkInstructionStatus.Archived, ct),
            OpenValidationActivities = await tr.CountAsync(x => x.Type == TrackingType.Validation && SequflohTrackingService.Open.Contains(x.Status), ct),
            FailedValidations = await Failed(tr, TrackingType.Validation).CountAsync(ct),
            AwaitingEngineeringReview = await AwaitingEngineering(tr).CountAsync(ct),
            ReleasedValidationInstructions = await wis.CountAsync(x => ValidationTypes.Contains(x.Type) && x.Status == WorkInstructionStatus.Released, ct),
            MyDevelopment = await WiRows(dev.Where(x => x.AssignedEngineerId == m.UserId || (x.AssignedEngineerId == null && x.AuthorId == m.UserId)), ct),
            AwaitingRows = await Rows(AwaitingEngineering(tr), ct),
        };
    }

    /// <summary>Quality or Engineering review queue: work instructions and tracking records waiting for that function, oldest first.</summary>
    [HttpGet("{area:regex(^(quality|engineering)$)}/review-queue")]
    public async Task<object> Queue(string area, [FromQuery] string? q, [FromQuery] string? kind, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(area == "quality" ? Permissions.SequflohViewQualityFolder : Permissions.SequflohViewEngineeringFolder, ct)) throw new ForbiddenException();
        var tr = await tracking.QueryAsync(ct);
        var wis = await instructions.QueryAsync(ct);
        var trq = area == "quality" ? tr.Where(x => x.Status == TrackingStatus.PendingInspection || (x.Status == TrackingStatus.PendingReview && x.QualityReviewRequired && x.QualityReviewedAt == null))
            : AwaitingEngineering(tr);
        var wiq = area == "quality" ? wis.Where(x => x.Status == WorkInstructionStatus.PendingReview || x.Status == WorkInstructionStatus.PendingApproval)
            : wis.Where(x => x.Status == WorkInstructionStatus.ChangesRequested || (x.Status == WorkInstructionStatus.PendingReview && ValidationTypes.Contains(x.Type)));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            trq = trq.Where(x => EF.Functions.ILike(x.Number + " " + x.WorkInstructionNumber + " " + (x.PartNumber ?? ""), like));
            wiq = wiq.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.PartNumber ?? ""), like));
        }
        var rows = new List<QueueRow>();
        if (kind is null or "tracking")
            rows.AddRange(await trq.Select(x => new QueueRow(x.Id, "Tracking record", x.Number, x.WorkInstructionNumber + " rev " + x.WorkInstructionRevision + " — " + x.WorkInstructionTitle, x.Type.ToString(),
                x.Status.ToString(), x.PartNumber, x.LastTransitionAt ?? x.UpdatedAt, x.DueAt, "/sequfloh/record/" + x.Id + "?tab=review")).Take(200).ToListAsync(ct));
        if (kind is null or "instruction")
            rows.AddRange(await wiq.Select(x => new QueueRow(x.Id, "Work instruction", x.Number + " rev " + x.Revision, x.Title, x.Type.ToString(), x.Status.ToString(), x.PartNumber, x.UpdatedAt, null,
                "/sequfloh/instruction/" + x.Id)).Take(200).ToListAsync(ct));
        return rows.OrderBy(r => r.WaitingSince);
    }
    public sealed record QueueRow(Guid Id, string Kind, string Number, string Title, string Type, string Status, string? PartNumber, DateTimeOffset WaitingSince, DateTimeOffset? DueAt, string Path);
}
