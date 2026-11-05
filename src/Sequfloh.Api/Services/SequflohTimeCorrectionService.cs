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
public sealed record PastTimePause(DateTimeOffset From, DateTimeOffset To, string? Reason = null);
public sealed record PastTimeInput(Guid? UserId, DateTimeOffset Start, DateTimeOffset Stop, List<PastTimePause>? Pauses, string? Reason);
/// <summary>Correct one person's time. An open entry takes a clock-off and/or a pause. A finished entry takes a new start and/or end.</summary>
public sealed record TimeAdjustInput(Guid TimeEntryId, string? Reason, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, DateTimeOffset? PausedAt);

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
        var reports = await db.SequflohChargebackReports.Where(c => c.TrackingRecordId != null && ids.Contains(c.TrackingRecordId.Value)).ToListAsync(ct);
        // reports that link several work orders keep them in a JSON list
        foreach (var id in ids)
            reports.AddRange(await db.SequflohChargebackReports.Where(c => c.LinkedWorkOrders != null && c.LinkedWorkOrders.Contains(id.ToString())).ToListAsync(ct));
        foreach (var c in reports.DistinctBy(c => c.Id))
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
        var zone = await m.ZoneAsync(r.SiteId, ct);
        var crew = await db.SequflohCrewSessions.AsNoTracking().IgnoreQueryFilters().Where(s => s.TrackingRecordId == id && (s.WorkerId != null || r.UseCrewTime)).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var time = await db.SequflohTimeEntries.AsNoTracking().IgnoreQueryFilters().Where(e => e.TrackingRecordId == id).OrderBy(e => e.StartedAt).ToListAsync(ct);
        var entries = crew.Select(s => new
        {
            Kind = s.WorkerId is null ? "crew" : "dayWorker", s.Id, Who = s.WorkerName ?? $"Crew of {s.CrewSize}", s.StartedAt, s.StoppedAt, Seconds = SequflohCrewService.Labor(s, now), s.Note,
            s.VoidedAt, s.VoidedBy, s.VoidReason, OriginalStartedAt = (DateTimeOffset?)null, OriginalStoppedAt = (DateTimeOffset?)null, OriginalPausedAt = (DateTimeOffset?)null,
            AdjustedAt = (DateTimeOffset?)null, AdjustedBy = (string?)null, AdjustReason = (string?)null, StaleNote = (string?)null,
        }).Concat(time.Select(e => new
        {
            Kind = "user", e.Id, Who = e.UserName, e.StartedAt, e.StoppedAt, Seconds = SequflohTimeService.Worked(e, now),
            Note = e.AddedBy is not null ? $"Added after the fact by {e.AddedBy}: {e.AddedReason}" : e.AdjustedAt is not null ? $"Corrected by {e.AdjustedBy}: {e.AdjustReason}" : null,
            e.VoidedAt, e.VoidedBy, e.VoidReason, e.OriginalStartedAt, e.OriginalStoppedAt, e.OriginalPausedAt, e.AdjustedAt, e.AdjustedBy, e.AdjustReason,
            StaleNote = e.StoppedAt is null ? SequflohTimeService.OpenNote(e.StartedAt, now, zone) : null,
        })).OrderBy(e => e.StartedAt).ToList();
        return new
        {
            CanTransfer = await m.CanAsync(Permissions.SequflohTransferTime, r.SiteId, r.DepartmentId, ct),
            CanRemove = await m.CanAsync(Permissions.SequflohRemoveTime, r.SiteId, r.DepartmentId, ct),
            CanAddPast = await m.CanAsync(Permissions.SequflohAddPastTime, r.SiteId, r.DepartmentId, ct),
            CanAdjust = await m.CanAsync(Permissions.SequflohAdjustTime, r.SiteId, r.DepartmentId, ct),
            r.UseCrewTime,
            Entries = entries,
        };
    }

    /// <summary>
    /// Sets the real clock-off or pause on an entry that was left open, or the start or end of a finished entry.
    /// The times before the first correction stay on the entry. Duration, variance and chargeback hours are worked out again from the corrected times.
    /// </summary>
    public async Task<object> AdjustAsync(Guid recordId, TimeAdjustInput x, CancellationToken ct)
    {
        var reason = Clean(x.Reason) ?? throw new AppException("Type the reason for this correction.");
        var r = await tracking.LoadAsync(recordId, true, ct);
        await m.RequireAsync(Permissions.SequflohAdjustTime, r.SiteId, r.DepartmentId, ct);
        var e = await db.SequflohTimeEntries.FirstOrDefaultAsync(t => t.Id == x.TimeEntryId && t.TrackingRecordId == recordId, ct) ?? throw new NotFoundException("Time entry");
        var open = e.StoppedAt is null;
        if (open)
        {
            if (x.StartedAt is not null) throw new AppException("Set the clock-off or the pause time. The start can be edited once the entry is clocked off.");
            if (x.StoppedAt is null && x.PausedAt is null) throw new AppException("Enter the clock-off time or the pause time.");
        }
        else
        {
            if (x.PausedAt is not null) throw new AppException("Edit the start or the end time of a finished entry.");
            if (x.StartedAt is null && x.StoppedAt is null) throw new AppException("Enter the start time or the end time.");
        }

        var beforeStart = e.StartedAt;
        var beforeStop = e.StoppedAt;
        var beforePause = e.PausedAt;
        var z = await m.ZoneAsync(r.SiteId, ct);
        var now = m.Now;
        static DateTimeOffset U(DateTimeOffset d) => d.ToUniversalTime();
        var nextStart = x.StartedAt is { } ns0 ? U(ns0) : e.StartedAt;
        var nextStop = x.StoppedAt is { } nt0 ? U(nt0) : e.StoppedAt;
        if (nextStop is { } end && end <= nextStart) throw new AppException("The end time must be after the start time.");
        if (nextStart > now.AddMinutes(1) || nextStop is { } fut && fut > now.AddMinutes(1) || x.PausedAt is { } pauseAt && U(pauseAt) > now.AddMinutes(1))
            throw new AppException("The time can't be in the future.");
        if (x.PausedAt is { } pause && U(pause) <= nextStart) throw new AppException("The pause time must be after the start time.");
        if (x.PausedAt is { } pause2 && nextStop is { } end2 && U(pause2) >= end2) throw new AppException("The pause time must be before the clock-off.");
        var ev = Events(e);
        if (open)
        {
            if (x.PausedAt is { } p)
            {
                var at = p.ToUniversalTime();
                var idx = e.PausedAt is null ? -1 : ev.FindLastIndex(v => v.Kind == "Pause");
                var stillOpen = idx >= 0 && ev.Skip(idx + 1).All(v => v.Kind != "Resume" && v.Kind != "Stop");
                if (stillOpen) ev[idx] = ev[idx] with { At = at };
                else ev.Add(new SequflohTimeService.TimeEvent("Pause", at, "Break"));
            }
            if (x.StoppedAt is { } s) ev.Add(new SequflohTimeService.TimeEvent("Stop", s.ToUniversalTime()));
        }
        else
        {
            if (x.StartedAt is { } ns)
            {
                var at = ns.ToUniversalTime();
                var i = ev.FindIndex(v => v.Kind == "Start");
                if (i < 0) ev.Insert(0, new SequflohTimeService.TimeEvent("Start", at));
                else ev[i] = ev[i] with { At = at };
            }
            if (x.StoppedAt is { } nt)
            {
                var at = nt.ToUniversalTime();
                var i = ev.FindLastIndex(v => v.Kind == "Stop");
                if (i < 0) ev.Add(new SequflohTimeService.TimeEvent("Stop", at));
                else ev[i] = ev[i] with { At = at };
            }
        }
        if (x.StartedAt is null)
        {
            var i = ev.FindIndex(v => v.Kind == "Start");
            if (i >= 0) ev[i] = ev[i] with { At = e.StartedAt };
        }
        ev = [.. ev.OrderBy(v => v.At).ThenBy(v => Rank(v.Kind))];
        CheckTimeline(ev, now);
        var start = ev[0].At;
        var stopEv = ev.LastOrDefault(v => v.Kind == "Stop");
        DateTimeOffset? stop = stopEv?.At;
        if (stop is { } stopAt && stopAt <= start) throw new AppException("The end time must be after the start time.");
        var clash = await db.SequflohTimeEntries.AsNoTracking().Where(o => o.Id != e.Id && o.UserId == e.UserId && o.StartedAt < (stop ?? DateTimeOffset.MaxValue) && (o.StoppedAt == null || o.StoppedAt > start))
            .OrderBy(o => o.StartedAt).FirstOrDefaultAsync(ct);
        if (clash is not null)
            throw new ConflictException($"{e.UserName} already has time on {clash.TrackingNumber} from {L(clash.StartedAt, z)} to {(clash.StoppedAt is { } cs ? L(cs, z) : "(open)")}, which overlaps {L(start, z)} to {(stop is { } st ? L(st, z) : "(open)")}. Nothing was changed.");

        e.StartedAt = start;
        e.StoppedAt = stop;
        Recompute(e, ev);
        if (e.StartedAt == beforeStart && e.StoppedAt == beforeStop && e.PausedAt == beforePause)
            throw new AppException("Change the start, the clock-off or the pause time.");
        if (e.AdjustedAt is null)
        {
            e.OriginalStartedAt = beforeStart;
            e.OriginalStoppedAt = beforeStop;
            e.OriginalPausedAt = beforePause;
        }
        e.AdjustedAt = now;
        e.AdjustedBy = me.UserName;
        e.AdjustReason = reason;

        var bits = new List<string>();
        if (x.StartedAt is not null && e.StartedAt != beforeStart) bits.Add($"clock-on on {r.Number} from {When(beforeStart, z)} to {When(e.StartedAt, z)}");
        if (x.PausedAt is not null && e.PausedAt != beforePause) bits.Add($"pause on {r.Number} from {When(beforePause, z, "(not paused)")} to {When(e.PausedAt, z, "(not paused)")}");
        if (x.StoppedAt is not null && e.StoppedAt != beforeStop) bits.Add($"clock-off on {r.Number} from {When(beforeStop, z)} to {When(e.StoppedAt, z)}");
        if (bits.Count == 0) bits.Add($"time on {r.Number}");
        f.Audit("TimeAdjusted", SequflohTrackingService.EntityType, r.Id, $"{me.UserName} changed {e.UserName}'s {string.Join(" and ", bits)}. Reason: {reason}", r.SiteId, reason,
            new { person = e.UserName, beforeStart, beforeStop, beforePause, e.StartedAt, e.StoppedAt, e.PausedAt, e.Seconds });

        var entries = await db.SequflohTimeEntries.Where(t => t.TrackingRecordId == r.Id).ToListAsync(ct);
        var crew = await SequflohCrewService.Counted(db, db.SequflohCrewSessions.AsNoTracking().Where(s => s.TrackingRecordId == r.Id)).ToListAsync(ct);
        var userSeconds = entries.Sum(t => SequflohTimeService.Worked(t, now));
        var crewLabor = crew.Sum(s => SequflohCrewService.Labor(s, now));
        if (r.MinBenchmarkMinutes is not null || r.MaxBenchmarkMinutes is not null)
            r.VarianceMinutes = SequflohTrackingService.Variance(new SequflohTrackingRecord { ActiveSeconds = userSeconds + crewLabor, MinBenchmarkMinutes = r.MinBenchmarkMinutes, MaxBenchmarkMinutes = r.MaxBenchmarkMinutes });
        var userHours = Math.Round(userSeconds / 3600m, 2);
        await FlagChargebacksAsync([r.Id], $"{H(userSeconds + crewLabor)} h after a time correction on {r.Number} by {me.UserName}", ct);
        foreach (var c in await db.SequflohChargebackReports.Where(c => c.TrackingRecordId == r.Id || (c.LinkedWorkOrders != null && c.LinkedWorkOrders.Contains(r.Id.ToString()))).ToListAsync(ct))
            ApplyChargebackHours(c, r.Id, userHours);
        await db.SaveChangesAsync(ct);
        return new { e.Id, e.StartedAt, e.StoppedAt, e.PausedAt, e.Seconds, Hours = H(e.StoppedAt is null ? SequflohTimeService.Worked(e, now) : e.Seconds), r.VarianceMinutes };
    }

    static List<SequflohTimeService.TimeEvent> Events(SequflohTimeEntry e)
    {
        var list = System.Text.Json.JsonSerializer.Deserialize<List<SequflohTimeService.TimeEvent>>(e.Events, SequflohJson.Options) ?? [];
        if (list.Count > 0) return list;
        list.Add(new("Start", e.StartedAt));
        if (e.StoppedAt is { } s) list.Add(new("Stop", s));
        return list;
    }

    static int Rank(string kind) => kind switch { "Start" => 0, "Pause" => 1, "Resume" => 2, "Stop" => 3, _ => 4 };

    static void CheckTimeline(List<SequflohTimeService.TimeEvent> ev, DateTimeOffset now)
    {
        if (ev.Count == 0 || ev[0].Kind != "Start") throw new AppException("The entry has to start with the clock-on.");
        for (var i = 1; i < ev.Count; i++)
            if (ev[i].At <= ev[i - 1].At) throw new AppException(ev[i].At <= ev[0].At && ev[^1].Kind == "Stop" ? "The end time must be after the start time." : "Each time has to be after the one before it.");
        if (ev.Any(v => v.At > now.AddMinutes(1))) throw new AppException("The time can't be in the future.");
        var paused = false;
        for (var i = 1; i < ev.Count; i++)
        {
            switch (ev[i].Kind)
            {
                case "Pause":
                    if (paused) throw new AppException("A pause can't start while the job is already paused.");
                    paused = true;
                    break;
                case "Resume":
                    if (!paused) throw new AppException("Clock back on only follows a pause.");
                    paused = false;
                    break;
                case "Stop" when i != ev.Count - 1:
                    throw new AppException("The clock-off has to be the last time.");
                case "Stop":
                    break;
                default:
                    throw new AppException("The time line has a step that doesn't fit.");
            }
        }
    }

    static void Recompute(SequflohTimeEntry e, List<SequflohTimeService.TimeEvent> ev)
    {
        long paused = 0;
        DateTimeOffset? pauseFrom = null;
        foreach (var v in ev)
        {
            if (v.Kind is "Resume" or "Stop")
            {
                if (pauseFrom is { } pf) paused += Math.Max(0, (long)(v.At - pf).TotalSeconds);
                pauseFrom = null;
            }
            else if (v.Kind == "Pause") pauseFrom = v.At;
        }
        e.PausedSeconds = paused;
        e.PausedAt = e.StoppedAt is null ? pauseFrom : null;
        if (e.StoppedAt is { } stop)
        {
            var worked = (long)(stop - e.StartedAt).TotalSeconds - paused;
            if (worked <= 0) throw new AppException("The end time must be after the start time, with some worked time left after pauses.");
            e.Seconds = worked;
        }
        e.Events = System.Text.Json.JsonSerializer.Serialize(ev, SequflohJson.Options);
    }

    static string When(DateTimeOffset? d, TimeZoneInfo z, string open = "(open)") => d is null ? open : L(d.Value, z);

    static void ApplyChargebackHours(SequflohChargebackReport c, Guid recordId, decimal userHours)
    {
        if (c.TrackingRecordId == recordId)
        {
            c.UserHours = userHours;
            c.TotalHours = Math.Round((c.CrewHours ?? 0) + userHours, 2);
            return;
        }
        var rows = SequflohChargebackService.LinkedOf(c.LinkedWorkOrders);
        var hit = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id != recordId) continue;
            var total = Math.Round(rows[i].CrewHours + userHours, 2);
            rows[i] = rows[i] with { UserHours = userHours, TotalHours = total };
            hit = true;
        }
        if (!hit) return;
        c.LinkedWorkOrders = System.Text.Json.JsonSerializer.Serialize(rows, SequflohJson.Options);
        c.UserHours = rows.Sum(w => w.UserHours);
        c.CrewHours = rows.Sum(w => w.CrewHours);
        c.TotalHours = rows.Sum(w => w.TotalHours);
    }

    // ---- add past time for a registered person ----

    /// <summary>True when a reason says what was done: at least 20 characters, several words, not one character or word repeated, not a placeholder.</summary>
    public static string? CheckDetailedReason(string? reason)
    {
        var t = string.Join(' ', (reason ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const string ask = "Type a detailed reason (at least 20 characters): explain what work was done and why it wasn't clocked.";
        if (t.Length < 20) return ask;
        var letters = t.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToList();
        if (letters.Distinct().Count() < 5) return ask;
        var words = t.ToLowerInvariant().Split(' ').Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToList();
        if (words.Count < 4 || words.Distinct().Count() < 3) return ask;
        string[] placeholders = ["test", "testing", "asdf", "na", "n/a", "none", "xxx", "reason", "time", "past time", "forgot", "see above", "no reason"];
        if (placeholders.Any(x => t.Equals(x, StringComparison.OrdinalIgnoreCase))) return ask;
        return null;
    }

    /// <summary>
    /// Adds time a registered person worked on a work order but never clocked. It is saved as that person's clocked time, so it counts in their hours,
    /// total labor hours, personnel (once per person) and chargebacks, and is marked as added after the fact with who added it and why.
    /// Like the other time corrections, it is allowed on closed work orders; saved chargeback reports of the order are flagged to be regenerated.
    /// </summary>
    public async Task<object> AddPastAsync(Guid recordId, PastTimeInput x, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(recordId, false, ct);
        await m.RequireAsync(Permissions.SequflohAddPastTime, r.SiteId, r.DepartmentId, ct);
        if (r.UseCrewTime) throw new ConflictException(SequflohCrewService.NamedBlockedMessage);
        if (CheckDetailedReason(x.Reason) is { } bad) throw new AppException(bad);
        var reason = string.Join(' ', x.Reason!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (reason.Length > 1000) reason = reason[..1000];
        var userId = x.UserId ?? me.UserId ?? throw new AppException("Choose the person.");
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Id, u.DisplayName }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Person");
        var start = x.Start.ToUniversalTime(); var stop = x.Stop.ToUniversalTime();
        var now = m.Now;
        if (stop <= start) throw new AppException("The stop time must be after the start time.");
        if (stop > now.AddMinutes(1)) throw new AppException("Past time can't end in the future. Use Clock on job for time happening now.");
        if ((stop - start).TotalHours > 24) throw new AppException("One entry can be at most 24 hours. Add one entry per day.");
        var pauses = (x.Pauses ?? []).Select(p => p with { From = p.From.ToUniversalTime(), To = p.To.ToUniversalTime() }).OrderBy(p => p.From).ToList();
        var last = start;
        foreach (var p in pauses)
        {
            if (p.To <= p.From) throw new AppException("Each pause must continue after it starts.");
            if (p.From < start || p.To > stop) throw new AppException("Pauses must be between the start and stop times.");
            if (p.From < last) throw new AppException("Pauses can't overlap.");
            last = p.To;
        }
        var paused = pauses.Sum(p => (long)(p.To - p.From).TotalSeconds);
        var worked = (long)(stop - start).TotalSeconds - paused;
        if (worked <= 0) throw new AppException("Pauses can't cover the whole entry.");
        var z = await m.ZoneAsync(r.SiteId, ct);
        string W(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, z).ToString("ddd MM/dd h:mm tt", CultureInfo.InvariantCulture);
        string T(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, z).ToString(TimeZoneInfo.ConvertTime(d, z).Date == TimeZoneInfo.ConvertTime(start, z).Date ? "h:mm tt" : "ddd MM/dd h:mm tt", CultureInfo.InvariantCulture);
        // the same person can't have two overlapping entries on one work order; time on other work orders is allowed (a warning on screen)
        var clash = await db.SequflohTimeEntries.AsNoTracking().Where(o => o.TrackingRecordId == r.Id && o.UserId == user.Id && o.StartedAt < stop && (o.StoppedAt == null || o.StoppedAt > start)).OrderBy(o => o.StartedAt).FirstOrDefaultAsync(ct);
        if (clash is not null)
            throw new ConflictException($"{user.DisplayName} already has time on {r.Number} from {W(clash.StartedAt)}{(clash.StoppedAt is { } cs ? $" to {W(cs)}" : " (still clocked on)")}, which overlaps {W(start)} – {T(stop)}. Change the times, or correct that entry first.");
        var events = new List<SequflohTimeService.TimeEvent> { new("Start", start) };
        foreach (var p in pauses) { events.Add(new("Pause", p.From, string.IsNullOrWhiteSpace(p.Reason) ? "Break" : p.Reason.Trim())); events.Add(new("Resume", p.To)); }
        events.Add(new("Stop", stop));
        var e = new SequflohTimeEntry
        {
            TrackingRecordId = r.Id, TrackingNumber = r.Number, UserId = user.Id, UserName = user.DisplayName, StartedAt = start, StoppedAt = stop, Seconds = worked, PausedSeconds = paused,
            Events = System.Text.Json.JsonSerializer.Serialize(events, SequflohJson.Options), AddedBy = me.UserName, AddedReason = reason,
        };
        db.SequflohTimeEntries.Add(e);
        var text = $"{me.UserName} added {H(worked)} h of past time for {user.DisplayName} on {r.Number} ({W(start)} – {T(stop)}{(paused > 0 ? $", paused {paused / 60} min" : "")}), reason: {reason}";
        f.Audit("PastTimeAdded", SequflohTrackingService.EntityType, r.Id, text, r.SiteId, reason, new { person = user.DisplayName, start, stop, pausedSeconds = paused, workedSeconds = worked });
        await FlagChargebacksAsync([r.Id], $"{H(worked)} h of past time added for {user.DisplayName} by {me.UserName}", ct);
        await db.SaveChangesAsync(ct);
        return new { e.Id, Hours = H(worked) };
    }

    /// <summary>The person's other time that overlaps the given times, on other work orders (shown as a warning; it does not block).</summary>
    public async Task<object> PastOverlapsAsync(Guid recordId, Guid userId, DateTimeOffset start, DateTimeOffset stop, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(recordId, false, ct);
        await m.RequireAsync(Permissions.SequflohAddPastTime, r.SiteId, r.DepartmentId, ct);
        var s = start.ToUniversalTime(); var t = stop.ToUniversalTime();
        return await db.SequflohTimeEntries.AsNoTracking().Where(o => o.TrackingRecordId != recordId && o.UserId == userId && o.StartedAt < t && (o.StoppedAt == null || o.StoppedAt > s))
            .OrderBy(o => o.StartedAt).Select(o => new { o.TrackingNumber, o.StartedAt, o.StoppedAt }).Take(10).ToListAsync(ct);
    }
}
