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
/// SEQUFLOH live execution: Dispatch Queue, Active Station, Paused Tasks, Completed Tasks, My Assigned Work, Overdue Work and Work History.
/// Task actions themselves go through the tracking endpoints, which check permission, lock and reload the task and refuse stale or repeated operations.
/// </summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohViewLiveExecution), SequflohEnabled, Route("api/sequfloh/execution")]
public sealed class SequflohExecutionController(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohFoundationService f) : ControllerBase
{
    static readonly string[] Views = ["dispatch", "active", "paused", "finished", "mine", "overdue", "station"];

    /// <summary>Tasks for one live execution view, with the actions the current user can take on each.</summary>
    [HttpGet("queue/{view}")]
    public async Task<object> Queue(string view, [FromQuery] string? q, [FromQuery] TrackingStatus? status, [FromQuery] TrackingType? type, [FromQuery] Guid? assigned, [FromQuery] string? part,
        [FromQuery] Guid? department, [FromQuery] Guid? site, [FromQuery] string? dateField, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? priority,
        [FromQuery] string? result, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (!Views.Contains(view)) throw new NotFoundException("Live execution view");
        return await SequflohTrackingQuery.ListAsync(db, m, tracking, f, new TrackingFilter(view, q, status, type, null, assigned, part, department, site, dateField, from, to, priority, result, sort, page, pageSize), true, ct);
    }

    /// <summary>Counts for the live execution views (used by the views' header and the dashboards).</summary>
    [HttpGet("counts")]
    public async Task<object> Counts(CancellationToken ct)
    {
        var query = await tracking.QueryAsync(ct);
        var now = await tracking.DbNowAsync(ct);
        var cutoff = now.AddHours(-(await f.GetSettingsAsync(ct)).Tracking.OverdueThresholdHours);
        var me = m.UserId;
        return new
        {
            Dispatch = await query.CountAsync(x => x.Status == TrackingStatus.NotStarted || x.Status == TrackingStatus.Assigned, ct),
            Active = await query.CountAsync(x => x.Status == TrackingStatus.InProgress, ct),
            Paused = await query.CountAsync(x => x.Status == TrackingStatus.Paused, ct),
            OnHold = await query.CountAsync(x => x.Status == TrackingStatus.OnHold, ct),
            Mine = await query.CountAsync(x => (x.AssignedUserId == me || db.SequflohTaskAssignees.Any(a => a.TrackingRecordId == x.Id && a.UserId == me)) && SequflohTrackingService.Open.Contains(x.Status), ct),
            Overdue = await query.CountAsync(x => SequflohTrackingService.Open.Contains(x.Status) && x.DueAt != null && x.DueAt < cutoff, ct),
        };
    }

    /// <summary>Work History: append-only task events of the tasks the user can see, with search, filters, sorting and paging.</summary>
    [HttpGet("events")]
    public async Task<object> Events([FromQuery] string? q, [FromQuery] string? eventType, [FromQuery] Guid? user, [FromQuery] TrackingType? type, [FromQuery] Guid? site,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var records = await tracking.QueryAsync(ct);
        if (type is not null) records = records.Where(r => r.Type == type);
        if (site is not null) records = records.Where(r => r.SiteId == site);
        var query = db.SequflohTaskEvents.AsNoTracking().Where(e => records.Any(r => r.Id == e.TrackingRecordId));
        if (!string.IsNullOrWhiteSpace(eventType)) query = query.Where(e => e.EventType == eventType);
        if (user is not null) query = query.Where(e => e.UserId == user);
        if (from is not null || to is not null)
        {
            var (fr, t) = await m.DayRangeAsync(site, from ?? DateOnly.MinValue.AddYears(2000), to ?? DateOnly.MaxValue.AddYears(-1), ct);
            query = query.Where(e => e.CreatedAt >= fr && e.CreatedAt < t);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.TrackingNumber + " " + e.UserName + " " + (e.Reason ?? "") + " " + (e.Comment ?? ""), like));
        }
        query = (sort ?? "createdAt:desc") switch
        {
            "createdAt:asc" => query.OrderBy(e => e.CreatedAt),
            "trackingNumber:asc" => query.OrderBy(e => e.TrackingNumber).ThenBy(e => e.CreatedAt), "trackingNumber:desc" => query.OrderByDescending(e => e.TrackingNumber).ThenBy(e => e.CreatedAt),
            "eventType:asc" => query.OrderBy(e => e.EventType).ThenByDescending(e => e.CreatedAt), "eventType:desc" => query.OrderByDescending(e => e.EventType).ThenByDescending(e => e.CreatedAt),
            "userName:asc" => query.OrderBy(e => e.UserName).ThenByDescending(e => e.CreatedAt), "userName:desc" => query.OrderByDescending(e => e.UserName).ThenByDescending(e => e.CreatedAt),
            _ => query.OrderByDescending(e => e.CreatedAt)
        };
        return await Paging.PageAsync(query.Select(e => new
        {
            e.Id, e.TrackingRecordId, e.TrackingNumber, e.EventType, e.FromStatus, e.ToStatus, e.CreatedAt, e.UserId, e.UserName, e.Reason, e.Comment, e.ActiveSeconds, e.PausedSeconds, e.HoldSeconds, e.OperationId,
            Type = db.SequflohTrackingRecords.Where(r => r.Id == e.TrackingRecordId).Select(r => r.Type).FirstOrDefault(),
            AssignedBefore = db.Users.Where(u => u.Id == e.AssignedBeforeId).Select(u => u.DisplayName).FirstOrDefault(),
            AssignedAfter = db.Users.Where(u => u.Id == e.AssignedAfterId).Select(u => u.DisplayName).FirstOrDefault()
        }), page, pageSize, ct);
    }
}
