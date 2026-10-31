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
        var crew = await db.SequflohCrewSessions.AsNoTracking().IgnoreQueryFilters().Where(s => s.TrackingRecordId == id && (s.WorkerId != null || r.UseCrewTime)).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var time = await db.SequflohTimeEntries.AsNoTracking().IgnoreQueryFilters().Where(e => e.TrackingRecordId == id).OrderBy(e => e.StartedAt).ToListAsync(ct);
        var entries = crew.Select(s => new
        {
            Kind = s.WorkerId is null ? "crew" : "dayWorker", s.Id, Who = s.WorkerName ?? $"Crew of {s.CrewSize}", s.StartedAt, s.StoppedAt, Seconds = SequflohCrewService.Labor(s, now), s.Note,
            s.VoidedAt, s.VoidedBy, s.VoidReason,
        }).Concat(time.Select(e => new
        {
            Kind = "user", e.Id, Who = e.UserName, e.StartedAt, e.StoppedAt, Seconds = SequflohTimeService.Worked(e, now), Note = e.AddedBy is null ? null : $"Added after the fact by {e.AddedBy}: {e.AddedReason}", e.VoidedAt, e.VoidedBy, e.VoidReason,
        })).OrderBy(e => e.StartedAt).ToList();
        return new
        {
            CanTransfer = await m.CanAsync(Permissions.SequflohTransferTime, r.SiteId, r.DepartmentId, ct),
            CanRemove = await m.CanAsync(Permissions.SequflohRemoveTime, r.SiteId, r.DepartmentId, ct),
            CanAddPast = await m.CanAsync(Permissions.SequflohAddPastTime, r.SiteId, r.DepartmentId, ct),
            r.UseCrewTime,
            Entries = entries,
        };
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
