using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Filters shared by the tracking folders and the live execution views.</summary>
public sealed record TrackingFilter(string? Folder = null, string? Q = null, TrackingStatus? Status = null, TrackingType? Type = null, Guid? Owner = null, Guid? Assigned = null,
    string? Part = null, Guid? Department = null, Guid? Site = null, string? DateField = null, DateOnly? From = null, DateOnly? To = null, string? Priority = null, string? Result = null,
    string? Sort = null, int Page = 1, int PageSize = 25, string? Tag = null);

/// <summary>
/// One listing for tracking records and tasks. Folders: validation, fai, repair, production, inspection, open, completed, on-hold, rejected, all (tracking) and
/// dispatch, active, paused, finished, mine, overdue, station (live execution). Rows are limited to the sites and departments the user can see; elapsed,
/// paused and hold times are calculated from the stored totals and the database clock. Live execution rows carry the actions the user can take now.
/// </summary>
internal static class SequflohTrackingQuery
{
    public static async Task<object> ListAsync(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohFoundationService f, TrackingFilter p, bool withActions, CancellationToken ct)
    {
        var query = await tracking.QueryAsync(ct);
        var now = await tracking.DbNowAsync(ct);
        var me = m.UserId;
        var overdueCutoff = now.AddHours(-(await f.GetSettingsAsync(ct)).Tracking.OverdueThresholdHours);
        query = (p.Folder ?? "all") switch
        {
            "validation" => query.Where(x => x.Type == TrackingType.Validation || x.Type == TrackingType.Fai),
            "salt-spray" => query.Where(x => x.Type == TrackingType.SaltSpray),
            "validation-fai" => query.Where(x => x.Type == TrackingType.Validation || x.Type == TrackingType.Fai),
            "fai" => query.Where(x => x.Type == TrackingType.Validation || x.Type == TrackingType.Fai),
            "repair" => query.Where(x => x.Type == TrackingType.Repair),
            "production" => query.Where(x => x.Type == TrackingType.Production),
            "inspection" => query.Where(x => x.Type == TrackingType.Inspection),
            "warranty" => query.Where(x => x.Type == TrackingType.Warranty),
            "development" => query.Where(x => x.Type == TrackingType.Development),
            "rebox" => query.Where(x => x.Type == TrackingType.Rebox),
            "test" => query.Where(x => x.Type == TrackingType.Test),
            "weight" => query.Where(x => x.Type == TrackingType.Weight),
            "research" => query.Where(x => x.Type == TrackingType.Research),
            "open" => query.Where(x => SequflohTrackingService.Open.Contains(x.Status)),
            "completed" => query.Where(x => x.Status == TrackingStatus.Completed),
            "on-hold" => query.Where(x => x.Status == TrackingStatus.Paused), // old links; holds are handled in the ERP
            "dispatch" => query.Where(x => x.Status == TrackingStatus.NotStarted || x.Status == TrackingStatus.Assigned),
            "active" => query.Where(x => x.Status == TrackingStatus.InProgress),
            "paused" => query.Where(x => x.Status == TrackingStatus.Paused),
            "finished" => query.Where(x => SequflohTrackingService.Terminal.Contains(x.Status)),
            "mine" => query.Where(x => (x.AssignedUserId == me || db.SequflohTaskAssignees.Any(a => a.TrackingRecordId == x.Id && a.UserId == me)) && SequflohTrackingService.Open.Contains(x.Status)),
            "overdue" => query.Where(x => SequflohTrackingService.Open.Contains(x.Status) && x.DueAt != null && x.DueAt < overdueCutoff),
            "station" => query.Where(x => (x.AssignedUserId == me || x.ActiveOperatorId == me || db.SequflohTaskAssignees.Any(a => a.TrackingRecordId == x.Id && a.UserId == me)) && (x.Status == TrackingStatus.Assigned || x.Status == TrackingStatus.InProgress
                || x.Status == TrackingStatus.Paused || x.Status == TrackingStatus.OnHold)),
            _ => query
        };
        if (p.Status is not null) query = query.Where(x => x.Status == p.Status);
        if (p.Type is { } pt) query = pt is TrackingType.Validation or TrackingType.Fai ? query.Where(x => x.Type == TrackingType.Validation || x.Type == TrackingType.Fai) : query.Where(x => x.Type == pt);
        if (p.Owner is not null) query = query.Where(x => x.OwnerId == p.Owner);
        if (p.Assigned is not null) query = query.Where(x => x.AssignedUserId == p.Assigned || db.SequflohTaskAssignees.Any(a => a.TrackingRecordId == x.Id && a.UserId == p.Assigned));
        if (p.Department is not null) query = query.Where(x => x.DepartmentId == p.Department);
        if (p.Site is not null) query = query.Where(x => x.SiteId == p.Site);
        if (!string.IsNullOrWhiteSpace(p.Priority)) query = query.Where(x => x.Priority == p.Priority);
        if (!string.IsNullOrWhiteSpace(p.Result)) query = query.Where(x => x.Result == p.Result);
        if (!string.IsNullOrWhiteSpace(p.Part)) query = query.Where(x => x.PartNumber != null && EF.Functions.ILike(x.PartNumber, $"%{p.Part.Trim()}%"));
        if (p.From is not null || p.To is not null)
        {
            var (fr, t) = await m.DayRangeAsync(p.Site, p.From ?? DateOnly.MinValue.AddYears(2000), p.To ?? DateOnly.MaxValue.AddYears(-1), ct);
            query = p.DateField switch
            {
                "due" => query.Where(x => x.DueAt >= fr && x.DueAt < t),
                "completed" => query.Where(x => x.ActualCompletion >= fr && x.ActualCompletion < t),
                "started" => query.Where(x => x.ActualStart >= fr && x.ActualStart < t),
                _ => query.Where(x => x.CreatedAt >= fr && x.CreatedAt < t)
            };
        }
        if (!string.IsNullOrWhiteSpace(p.Q))
        {
            var like = $"%{p.Q.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.WorkInstructionNumber + " " + x.WorkInstructionTitle + " " + (x.PartNumber ?? "") + " " + (x.SerialNumber ?? "") + " "
                + (x.LotNumber ?? "") + " " + (x.WorkOrder ?? "") + " " + (x.RepairOrder ?? "") + " " + (x.Project ?? "") + " " + (x.Operation ?? "") + " " + (x.FamilyName ?? "") + " " + (x.InspectionDescription ?? ""), like));
        }
        var defaultSort = p.Folder switch { "dispatch" or "mine" => "queue", "overdue" => "dueAt:asc", "finished" => "actualCompletion:desc", "paused" => "pausedAt:asc", "station" => "station", _ => "createdAt:desc" };
        query = (p.Sort ?? defaultSort) switch
        {
            "number:asc" => query.OrderBy(x => x.Number), "number:desc" => query.OrderByDescending(x => x.Number),
            "status:asc" => query.OrderBy(x => x.Status), "status:desc" => query.OrderByDescending(x => x.Status),
            "priority:asc" => query.OrderBy(x => x.Priority), "priority:desc" => query.OrderByDescending(x => x.Priority),
            "dueAt:asc" => query.OrderBy(x => x.DueAt == null).ThenBy(x => x.DueAt), "dueAt:desc" => query.OrderByDescending(x => x.DueAt),
            "partNumber:asc" => query.OrderBy(x => x.PartNumber), "partNumber:desc" => query.OrderByDescending(x => x.PartNumber),
            "actualCompletion:asc" => query.OrderBy(x => x.ActualCompletion), "actualCompletion:desc" => query.OrderByDescending(x => x.ActualCompletion),
            "pausedAt:asc" => query.OrderBy(x => x.CurrentStateStartedAt), "pausedAt:desc" => query.OrderByDescending(x => x.CurrentStateStartedAt),
            "updatedAt:asc" => query.OrderBy(x => x.UpdatedAt), "updatedAt:desc" => query.OrderByDescending(x => x.UpdatedAt),
            "station" => query.OrderBy(x => x.Status == TrackingStatus.InProgress ? 0 :  x.Status == TrackingStatus.Paused ? 2 : x.Status == TrackingStatus.OnHold ? 3 : 4)
                .ThenBy(x => x.DueAt == null).ThenBy(x => x.DueAt),
            // the work queue: higher priority first, then the earliest due date
            "queue" => query.OrderBy(x => x.Priority == "Critical" || x.Priority == "Urgent" ? 0 : x.Priority == "High" ? 1 : x.Priority == "Low" ? 3 : 2)
                .ThenBy(x => x.DueAt == null).ThenBy(x => x.DueAt).ThenBy(x => x.CreatedAt),
            "createdAt:asc" => query.OrderBy(x => x.CreatedAt), _ => query.OrderByDescending(x => x.CreatedAt)
        };
        var paged = await Paging.PageAsync(query, p.Page, p.PageSize, ct);
        var pageIds = paged.Items.Select(x => x.Id).ToList();
        var team = (await db.SequflohTaskAssignees.AsNoTracking().Where(a => pageIds.Contains(a.TrackingRecordId)).OrderBy(a => a.AddedAt).ToListAsync(ct))
            .GroupBy(a => a.TrackingRecordId).ToDictionary(g => g.Key, g => g.Select(a => a.UserId).ToList());
        var userIds = paged.Items.SelectMany(x => new[] { x.OwnerId, x.AssignedUserId, x.CompletedById, x.ActiveOperatorId }).OfType<Guid>().Concat(team.Values.SelectMany(v => v)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var deptIds = paged.Items.Select(x => x.DepartmentId).OfType<Guid>().Distinct().ToList();
        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var siteIds = paged.Items.Select(x => x.SiteId).Distinct().ToList();
        var sites = await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        string? U(Guid? id) => id is { } g && users.TryGetValue(g, out var n) ? n : null;
        var items = new List<object>();
        foreach (var x in paged.Items)
        {
            var (active, paused, hold) = SequflohTrackingService.Durations(x, now);
            items.Add(new
            {
                x.Id, x.Number, x.Type, x.Status, x.Result, x.Priority, x.WorkInstructionId, x.WorkInstructionNumber, x.WorkInstructionRevision, x.WorkInstructionTitle,
                x.PartNumber, x.PartDescription, x.SerialNumber, x.LotNumber, x.WorkOrder, x.RepairOrder, x.Operation, x.PlannedStart, x.DueAt, x.ActualStart, x.ActualCompletion, x.CreatedAt, x.UpdatedAt,
                x.MinBenchmarkMinutes, x.MaxBenchmarkMinutes, x.VarianceMinutes, x.VarianceReason, x.StateVersion, x.AssignedUserId, x.PauseReason, x.HoldReason, x.FailureReason, x.CancelReason,
                PausedAt = x.Status == TrackingStatus.Paused ? x.CurrentStateStartedAt : null, HeldAt = x.Status == TrackingStatus.OnHold ? x.CurrentStateStartedAt : null,
                Owner = U(x.OwnerId), AssignedUser = U(x.AssignedUserId),
                Assignees = (team.TryGetValue(x.Id, out var tm) ? tm : x.AssignedUserId is { } lu ? [lu] : []).Select(u => U(u)).OfType<string>().ToList(), CompletedBy = U(x.CompletedById), ActiveOperator = U(x.ActiveOperatorId),
                Department = x.DepartmentId is { } d && depts.TryGetValue(d, out var dn) ? dn : null, Site = sites.TryGetValue(x.SiteId, out var sc) ? sc : null,
                ElapsedSeconds = active, PausedSeconds = paused, HoldSeconds = hold,
                DurationSeconds = active + paused + hold,
                OverdueSeconds = x.DueAt is { } due && due < now && SequflohTrackingService.Open.Contains(x.Status) ? (long)(now - due).TotalSeconds : 0,
                Actions = withActions ? await tracking.AvailableActionsAsync(x, ct) : null
            });
        }
        return new { Items = items, paged.Total, ServerTime = now };
    }
}
