using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record TimeSelection(List<Guid>? CrewSessionIds, List<Guid>? TimeEntryIds);
public sealed record TimeTransferInput(Guid TargetId, List<Guid>? CrewSessionIds, List<Guid>? TimeEntryIds, string? Reason);
public sealed record TimeRemoveInput(List<Guid>? CrewSessionIds, List<Guid>? TimeEntryIds, string? Reason);

/// <summary>
/// Corrections to recorded time: move entries intact to another work order, or remove them with a reason. Removed time is voided, not deleted:
/// it no longer counts on totals, job reports or chargebacks but stays in the record's history. Both orders get audit entries, and saved
/// chargeback reports of either order are flagged to be regenerated.
/// </summary>
public sealed class SequflohTimeCorrectionService(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohFoundationService f, ICurrentUser me)
{
    sealed record Picked(List<SequflohCrewSession> Crew, List<SequflohTimeEntry> Time);

    async Task<Picked> PickAsync(Guid recordId, List<Guid>? crewIds, List<Guid>? timeIds, CancellationToken ct)
    {
        var ci = crewIds ?? []; var ti = timeIds ?? [];
        if (ci.Count + ti.Count == 0) throw new AppException("Select the time to change.");
        var crew = await db.SequflohCrewSessions.Where(s => s.TrackingRecordId == recordId && ci.Contains(s.Id)).ToListAsync(ct);
        var time = await db.SequflohTimeEntries.Where(e => e.TrackingRecordId == recordId && ti.Contains(e.Id)).ToListAsync(ct);
        if (crew.Count != ci.Distinct().Count() || time.Count != ti.Distinct().Count()) throw new NotFoundException("Time entry");
        if (crew.Any(s => s.StoppedAt is null) || time.Any(e => e.StoppedAt is null))
            throw new ConflictException("Some of the selected time is still clocked on. Clock it off first.");
        return new(crew, time);
    }

    static string L(DateTimeOffset d, TimeZoneInfo z) => TimeZoneInfo.ConvertTime(d, z).ToString("MM/dd/yyyy h:mm tt", CultureInfo.InvariantCulture);
    static string H(long s) => SequflohCrewService.H(s);

    static string Line(SequflohCrewSession s, TimeZoneInfo z, DateTimeOffset now) =>
        $"{(s.WorkerName is { } n ? $"day worker {n}" : $"crew of {s.CrewSize}")} {L(s.StartedAt, z)} - {L(s.StoppedAt!.Value, z)}, {H(SequflohCrewService.Labor(s, now))} labor h";
    static string Line(SequflohTimeEntry e, TimeZoneInfo z, DateTimeOffset now) =>
        $"{e.UserName} {L(e.StartedAt, z)} - {L(e.StoppedAt!.Value, z)}, {H(SequflohTimeService.Worked(e, now))} h";

    static long Seconds(Picked p, DateTimeOffset now) => p.Crew.Sum(s => SequflohCrewService.Labor(s, now)) + p.Time.Sum(e => SequflohTimeService.Worked(e, now));

    async Task FlagChargebacksAsync(IEnumerable<Guid> recordIds, string note, CancellationToken ct)
    {
        var ids = recordIds.ToList();
        foreach (var c in await db.SequflohChargebackReports.Where(c => c.TrackingRecordId != null && ids.Contains(c.TrackingRecordId.Value)).ToListAsync(ct))
        {
            c.TimeChangedAt = m.Now; c.TimeChangedNote = note.Length > 500 ? note[..500] : note;
            f.Audit("ChargebackTimeChanged", "SequflohChargebackReport", c.Id, $"Chargeback report {c.WorkOrderNumber}: time on its work order changed after it was created ({note}). Regenerate it.", c.SiteId);
        }
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 500)];

    public async Task<object> TransferAsync(Guid sourceId, TimeTransferInput x, CancellationToken ct)
    {
        if (x.TargetId == sourceId) throw new AppException("Choose a different work order to move the time to.");
        var src = await tracking.LoadAsync(sourceId, false, ct);
        await m.RequireAsync(Permissions.SequflohTransferTime, src.SiteId, src.DepartmentId, ct);
        var dst = await tracking.LoadAsync(x.TargetId, false, ct);
        await m.RequireAsync(Permissions.SequflohTransferTime, dst.SiteId, dst.DepartmentId, ct);
        var p = await PickAsync(sourceId, x.CrewSessionIds, x.TimeEntryIds, ct);
        var z = await m.ZoneAsync(src.SiteId, ct);
        var now = m.Now;

        // the same person can't be on two overlapping entries on the target; nameless crew rows have no person and never clash
        foreach (var e in p.Time)
        {
            var clash = await db.SequflohTimeEntries.AsNoTracking().Where(o => o.TrackingRecordId == dst.Id && o.UserId == e.UserId && o.StartedAt < e.StoppedAt && (o.StoppedAt == null || o.StoppedAt > e.StartedAt)).FirstOrDefaultAsync(ct);
            if (clash is not null) throw new ConflictException($"{e.UserName} already has time on {dst.Number} from {L(clash.StartedAt, z)}{(clash.StoppedAt is { } cs ? $" to {L(cs, z)}" : " (still clocked on)")}, which overlaps {L(e.StartedAt, z)} - {L(e.StoppedAt!.Value, z)}. Nothing was moved.");
        }
        var targetWorkers = await db.SequflohDayWorkers.Where(w => w.TrackingRecordId == dst.Id).ToListAsync(ct);
        foreach (var s in p.Crew.Where(s => s.WorkerId != null))
        {
            var tw = targetWorkers.FirstOrDefault(w => string.Equals(w.Name, s.WorkerName, StringComparison.OrdinalIgnoreCase));
            if (tw is null) continue;
            var clash = await db.SequflohCrewSessions.AsNoTracking().Where(o => o.TrackingRecordId == dst.Id && o.WorkerId == tw.Id && o.StartedAt < s.StoppedAt && (o.StoppedAt == null || o.StoppedAt > s.StartedAt)).FirstOrDefaultAsync(ct);
            if (clash is not null) throw new ConflictException($"{s.WorkerName} already has time on {dst.Number} from {L(clash.StartedAt, z)}{(clash.StoppedAt is { } cs ? $" to {L(cs, z)}" : " (still clocked on)")}, which overlaps {L(s.StartedAt, z)} - {L(s.StoppedAt!.Value, z)}. Nothing was moved.");
        }

        var lines = p.Crew.Select(s => Line(s, z, now)).Concat(p.Time.Select(e => Line(e, z, now))).ToList();
        var hours = H(Seconds(p, now));
        foreach (var s in p.Crew)
        {
            if (s.WorkerId is not null)
            {
                var tw = targetWorkers.FirstOrDefault(w => string.Equals(w.Name, s.WorkerName, StringComparison.OrdinalIgnoreCase));
                if (tw is null)
                {
                    tw = new SequflohDayWorker { TrackingRecordId = dst.Id, TrackingNumber = dst.Number, Name = s.WorkerName ?? "Day worker" };
                    db.SequflohDayWorkers.Add(tw); targetWorkers.Add(tw);
                    f.Audit("DayWorkerAdded", SequflohTrackingService.EntityType, dst.Id, $"Day worker {tw.Name} added to {dst.Number} with time moved from {src.Number}", dst.SiteId, null, tw.Name);
                }
                s.WorkerId = tw.Id; s.WorkerName = tw.Name;
            }
            s.TrackingRecordId = dst.Id; s.TrackingNumber = dst.Number;
        }
        foreach (var e in p.Time) { e.TrackingRecordId = dst.Id; e.TrackingNumber = dst.Number; }
        var reason = Clean(x.Reason);
        var detail = string.Join("; ", lines);
        var tail = reason is null ? "" : $" Reason: {reason}";
        f.Audit("TimeTransferredOut", SequflohTrackingService.EntityType, src.Id, $"{me.UserName} moved {hours} h ({detail}) from {src.Number} to {dst.Number}.{tail}", src.SiteId, reason, new { to = dst.Number, entries = lines });
        f.Audit("TimeTransferredIn", SequflohTrackingService.EntityType, dst.Id, $"{me.UserName} moved {hours} h ({detail}) from {src.Number} to {dst.Number}.{tail}", dst.SiteId, reason, new { from = src.Number, entries = lines });
        await FlagChargebacksAsync([src.Id, dst.Id], $"{hours} h moved from {src.Number} to {dst.Number} on {L(now, z)} by {me.UserName}", ct);
        await db.SaveChangesAsync(ct);
        return new { Moved = lines.Count, Hours = hours, To = dst.Number, ToId = dst.Id };
    }

    public async Task<object> RemoveAsync(Guid sourceId, TimeRemoveInput x, CancellationToken ct)
    {
        var reason = Clean(x.Reason) ?? throw new AppException("Type the reason for removing this time.");
        var src = await tracking.LoadAsync(sourceId, false, ct);
        await m.RequireAsync(Permissions.SequflohRemoveTime, src.SiteId, src.DepartmentId, ct);
        var p = await PickAsync(sourceId, x.CrewSessionIds, x.TimeEntryIds, ct);
        var z = await m.ZoneAsync(src.SiteId, ct);
        var now = m.Now;
        var lines = p.Crew.Select(s => Line(s, z, now)).Concat(p.Time.Select(e => Line(e, z, now))).ToList();
        var hours = H(Seconds(p, now));
        foreach (var s in p.Crew) { s.VoidedAt = now; s.VoidedBy = me.UserName; s.VoidReason = reason; }
        foreach (var e in p.Time) { e.VoidedAt = now; e.VoidedBy = me.UserName; e.VoidReason = reason; }
        f.Audit("TimeRemoved", SequflohTrackingService.EntityType, src.Id, $"{me.UserName} removed {hours} h ({string.Join("; ", lines)}) from {src.Number}. Reason: {reason}", src.SiteId, reason, new { entries = lines });
        await FlagChargebacksAsync([src.Id], $"{hours} h removed from {src.Number} on {L(now, z)} by {me.UserName}", ct);
        await db.SaveChangesAsync(ct);
        return new { Removed = lines.Count, Hours = hours };
    }

    /// <summary>Every finished entry on the order for the correction list, the removed ones from the history, and what the reader may do.</summary>
    public async Task<object> ListAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var now = m.Now;
        var crew = await db.SequflohCrewSessions.AsNoTracking().IgnoreQueryFilters().Where(s => s.TrackingRecordId == id).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var time = await db.SequflohTimeEntries.AsNoTracking().IgnoreQueryFilters().Where(e => e.TrackingRecordId == id).OrderBy(e => e.StartedAt).ToListAsync(ct);
        var entries = crew.Select(s => new
        {
            Kind = s.WorkerId is null ? "crew" : "dayWorker", s.Id, Who = s.WorkerName ?? $"Crew of {s.CrewSize}", s.StartedAt, s.StoppedAt, Seconds = SequflohCrewService.Labor(s, now), s.Note,
            s.VoidedAt, s.VoidedBy, s.VoidReason,
        }).Concat(time.Select(e => new
        {
            Kind = "user", e.Id, Who = e.UserName, e.StartedAt, e.StoppedAt, Seconds = SequflohTimeService.Worked(e, now), Note = (string?)null, e.VoidedAt, e.VoidedBy, e.VoidReason,
        })).OrderBy(e => e.StartedAt).ToList();
        return new
        {
            CanTransfer = await m.CanAsync(Permissions.SequflohTransferTime, r.SiteId, r.DepartmentId, ct),
            CanRemove = await m.CanAsync(Permissions.SequflohRemoveTime, r.SiteId, r.DepartmentId, ct),
            Entries = entries,
        };
    }
}
