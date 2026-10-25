using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record CrewPause(DateTimeOffset From, DateTimeOffset To);
public sealed record CrewSessionInput(int CrewSize, DateTimeOffset Start, DateTimeOffset Stop, List<CrewPause>? Pauses, string? Note, Guid? WorkerId = null);
public sealed record CrewBulkInput(DateOnly From, DateOnly To, int CrewSize, decimal HoursPerDay, string? StartTime = null, int LunchMinutes = 0, bool SkipWeekends = true,
    List<DateOnly>? Days = null, string? Note = null);

/// <summary>
/// Crew time on a work order: a number of workers with no names or accounts, clocked together. Registered people keep clocking on and off with their own time;
/// crew hours and their hours are shown separately and added together. Every change is permission-checked and written to the audit trail with before and after.
/// </summary>
public sealed class SequflohCrewService(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohFoundationService f)
{
    public const int MaxCrew = 500;
    public sealed record CrewEvent(string Kind, DateTimeOffset At);

    public static List<CrewEvent> EventsOf(SequflohCrewSession s) => JsonSerializer.Deserialize<List<CrewEvent>>(s.Events, SequflohJson.Options) ?? [];
    static void Add(SequflohCrewSession s, string kind, DateTimeOffset at) { var l = EventsOf(s); l.Add(new(kind, at)); s.Events = JsonSerializer.Serialize(l, SequflohJson.Options); }

    /// <summary>Elapsed seconds from start to stop (or now), paused time included.</summary>
    public static long Elapsed(SequflohCrewSession s, DateTimeOffset now) => Math.Max(0, (long)((s.StoppedAt ?? now) - s.StartedAt).TotalSeconds);
    public static long Paused(SequflohCrewSession s, DateTimeOffset now) => s.PausedSeconds + (s.StoppedAt is null && s.PausedAt is { } p ? Math.Max(0, (long)(now - p).TotalSeconds) : 0);
    /// <summary>Worked seconds for the crew as a whole (elapsed minus paused).</summary>
    public static long Worked(SequflohCrewSession s, DateTimeOffset now) => Math.Max(0, Elapsed(s, now) - Paused(s, now));
    /// <summary>Labor seconds: worked time times the crew size of this session.</summary>
    public static long Labor(SequflohCrewSession s, DateTimeOffset now) => Worked(s, now) * s.CrewSize;

    public static string H(long seconds) => (seconds / 3600m).ToString("0.00", CultureInfo.InvariantCulture);

    async Task<SequflohTrackingRecord> EditableAsync(Guid id, CancellationToken ct, bool clockingOff = false)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        await m.RequireAsync(Permissions.SequflohManageCrewTime, r.SiteId, r.DepartmentId, ct);
        // a crew still clocked on when the work order closed can always be clocked off, so its time is kept
        if (!clockingOff) SequflohTrackingService.RequireOpen(r);
        return r;
    }

    static string Describe(SequflohCrewSession s, TimeZoneInfo z)
    {
        string L(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, z).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var end = s.StoppedAt is { } st ? L(st) : "running";
        var who = s.WorkerName is { } wn ? wn : $"{s.CrewSize} worker{(s.CrewSize == 1 ? "" : "s")}";
        return $"{who}, {L(s.StartedAt)} to {end}, paused {s.PausedSeconds / 60} min, {H(Labor(s, s.StoppedAt ?? s.StartedAt))} labor hours";
    }

    void Audit(SequflohTrackingRecord r, string action, string text, string? before, string? after) =>
        f.Audit(action, SequflohTrackingService.EntityType, r.Id, text, r.SiteId, context: new { before, after });

    static void CheckCrew(int n) { if (n < 1 || n > MaxCrew) throw new AppException($"Crew size must be between 1 and {MaxCrew}."); }

    async Task CheckOverlapAsync(Guid recordId, Guid? self, DateTimeOffset start, DateTimeOffset? stop, DateTimeOffset now, CancellationToken ct, Guid? worker = null)
    {
        var end = stop ?? DateTimeOffset.MaxValue;
        var others = await db.SequflohCrewSessions.AsNoTracking().Where(x => x.TrackingRecordId == recordId && x.Id != self && x.WorkerId == worker).ToListAsync(ct);
        var clash = others.FirstOrDefault(o => o.StartedAt < end && (o.StoppedAt ?? DateTimeOffset.MaxValue) > start);
        if (clash is not null) throw new ConflictException($"These times overlap {(worker is null ? "crew" : "this worker's")} time already on this work order ({clash.StartedAt:yyyy-MM-dd HH:mm} UTC). Change the times or edit that entry.");
    }

    /// <summary>Checks an entered session and fills the session from it.</summary>
    static void Apply(SequflohCrewSession s, CrewSessionInput x, DateTimeOffset now)
    {
        x = x with { Start = x.Start.ToUniversalTime(), Stop = x.Stop.ToUniversalTime(), Pauses = x.Pauses?.Select(p => new CrewPause(p.From.ToUniversalTime(), p.To.ToUniversalTime())).ToList() };
        CheckCrew(x.CrewSize);
        if (x.Stop <= x.Start) throw new AppException("The stop time must be after the start time.");
        if (x.Stop > now.AddMinutes(5)) throw new AppException("Entered crew time can't end in the future. Use Start for time happening now.");
        if ((x.Stop - x.Start).TotalHours > 24) throw new AppException("One crew entry can be at most 24 hours. Use bulk entry for several days.");
        var pauses = (x.Pauses ?? []).OrderBy(p => p.From).ToList();
        DateTimeOffset last = x.Start;
        foreach (var p in pauses)
        {
            if (p.To <= p.From) throw new AppException("Each pause must continue after it starts.");
            if (p.From < x.Start || p.To > x.Stop) throw new AppException("Pauses must be between the start and stop times.");
            if (p.From < last) throw new AppException("Pauses can't overlap.");
            last = p.To;
        }
        if (pauses.Sum(p => (p.To - p.From).TotalSeconds) >= (x.Stop - x.Start).TotalSeconds) throw new AppException("Pauses can't cover the whole entry. Worker time can't be set to zero.");
        s.CrewSize = x.CrewSize; s.StartedAt = x.Start; s.StoppedAt = x.Stop; s.PausedAt = null;
        s.PausedSeconds = pauses.Sum(p => (long)(p.To - p.From).TotalSeconds);
        var ev = new List<CrewEvent> { new("Start", x.Start) };
        foreach (var p in pauses) { ev.Add(new("Pause", p.From)); ev.Add(new("Continue", p.To)); }
        ev.Add(new("Stop", x.Stop));
        s.Events = JsonSerializer.Serialize(ev, SequflohJson.Options);
        s.Entered = true;
        s.Note = string.IsNullOrWhiteSpace(x.Note) ? null : x.Note.Trim()[..Math.Min(x.Note.Trim().Length, 300)];
    }

    // ---- live ----

    async Task<SequflohDayWorker?> WorkerAsync(Guid id, Guid? worker, CancellationToken ct) => worker is null ? null
        : await db.SequflohDayWorkers.FirstOrDefaultAsync(w => w.Id == worker && w.TrackingRecordId == id, ct) ?? throw new NotFoundException("Day worker");
    static string Who(SequflohCrewSession s) => s.WorkerName ?? $"Crew of {s.CrewSize}";

    public async Task StartAsync(Guid id, int crewSize, CancellationToken ct, Guid? workerId = null)
    {
        var r = await EditableAsync(id, ct);
        var w = await WorkerAsync(id, workerId, ct);
        if (w is not null) crewSize = 1;
        CheckCrew(crewSize);
        var now = m.Now;
        if (await db.SequflohCrewSessions.AnyAsync(x => x.TrackingRecordId == id && x.WorkerId == workerId && x.StoppedAt == null, ct)) throw new ConflictException(w is null ? "The crew is already clocked on this job." : $"{w.Name} is already clocked on this job.");
        await CheckOverlapAsync(id, null, now, null, now, ct, workerId);
        var s = new SequflohCrewSession { TrackingRecordId = id, TrackingNumber = r.Number, CrewSize = crewSize, StartedAt = now, WorkerId = w?.Id, WorkerName = w?.Name };
        Add(s, "Start", now);
        db.SequflohCrewSessions.Add(s);
        Audit(r, "CrewStarted", $"{Who(s)} clocked on {r.Number}", null, Describe(s, await m.ZoneAsync(r.SiteId, ct)));
        await db.SaveChangesAsync(ct);
    }

    async Task<(SequflohTrackingRecord, SequflohCrewSession)> RunningAsync(Guid id, Guid? worker, CancellationToken ct, bool clockingOff = false)
    {
        var r = await EditableAsync(id, ct, clockingOff);
        var s = await db.SequflohCrewSessions.FirstOrDefaultAsync(x => x.TrackingRecordId == id && x.WorkerId == worker && x.StoppedAt == null, ct) ?? throw new ConflictException(worker is null ? "The crew is not clocked on this job." : "This worker is not clocked on this job.");
        return (r, s);
    }

    public async Task PauseAsync(Guid id, CancellationToken ct, Guid? workerId = null)
    {
        var (r, s) = await RunningAsync(id, workerId, ct);
        if (s.PausedAt is not null) throw new ConflictException("The crew is already paused.");
        s.PausedAt = m.Now; Add(s, "Pause", s.PausedAt.Value);
        Audit(r, "CrewPaused", $"{Who(s)} paused on {r.Number}", null, null);
        await db.SaveChangesAsync(ct);
    }

    public async Task ContinueAsync(Guid id, CancellationToken ct, Guid? workerId = null)
    {
        var (r, s) = await RunningAsync(id, workerId, ct);
        if (s.PausedAt is not { } p) throw new ConflictException("The crew is not paused.");
        var now = m.Now;
        s.PausedSeconds += Math.Max(0, (long)(now - p).TotalSeconds); s.PausedAt = null; Add(s, "Continue", now);
        Audit(r, "CrewContinued", $"{Who(s)} continued on {r.Number}", null, null);
        await db.SaveChangesAsync(ct);
    }

    public async Task StopAsync(Guid id, CancellationToken ct, Guid? workerId = null)
    {
        var (r, s) = await RunningAsync(id, workerId, ct, clockingOff: true);
        var now = m.Now;
        if (s.PausedAt is { } p) { s.PausedSeconds += Math.Max(0, (long)(now - p).TotalSeconds); s.PausedAt = null; Add(s, "Continue", now); }
        s.StoppedAt = now; Add(s, "Stop", now);
        Audit(r, "CrewStopped", $"{Who(s)} clocked off {r.Number}: {H(Labor(s, now))} labor hours", null, Describe(s, await m.ZoneAsync(r.SiteId, ct)));
        await db.SaveChangesAsync(ct);
    }

    // ---- entered (backdated) ----

    public async Task<Guid> AddAsync(Guid id, CrewSessionInput x, CancellationToken ct)
    {
        var r = await EditableAsync(id, ct);
        var now = m.Now;
        var w = await WorkerAsync(id, x.WorkerId, ct);
        var s = new SequflohCrewSession { TrackingRecordId = id, TrackingNumber = r.Number, WorkerId = w?.Id, WorkerName = w?.Name };
        Apply(s, w is null ? x : x with { CrewSize = 1 }, now);
        await CheckOverlapAsync(id, null, s.StartedAt, s.StoppedAt, now, ct, s.WorkerId);
        db.SequflohCrewSessions.Add(s);
        Audit(r, "CrewTimeAdded", $"Crew time added on {r.Number}: {Describe(s, await m.ZoneAsync(r.SiteId, ct))}", null, Describe(s, await m.ZoneAsync(r.SiteId, ct)));
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    async Task<(SequflohTrackingRecord, SequflohCrewSession)> SessionAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await db.SequflohCrewSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new NotFoundException("Crew time");
        return (await EditableAsync(s.TrackingRecordId, ct), s);
    }

    public async Task UpdateAsync(Guid sessionId, CrewSessionInput x, CancellationToken ct)
    {
        var (r, s) = await SessionAsync(sessionId, ct);
        if (s.StoppedAt is null) throw new ConflictException(s.WorkerId is null ? "Clock the crew off before editing this entry." : "Clock this worker off before editing this entry.");
        var z = await m.ZoneAsync(r.SiteId, ct);
        var before = Describe(s, z);
        Apply(s, s.WorkerId is null ? x : x with { CrewSize = 1 }, m.Now);
        await CheckOverlapAsync(r.Id, s.Id, s.StartedAt, s.StoppedAt, m.Now, ct, s.WorkerId);
        var after = Describe(s, z);
        Audit(r, "CrewTimeEdited", $"Crew time on {r.Number} changed from {before} to {after}", before, after);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recorded time is never deleted, because it must stay on the chargeback report. Correct it by editing the times.</summary>
    public Task DeleteAsync(Guid sessionId, CancellationToken ct) =>
        throw new ConflictException("Recorded time can't be deleted. Edit the times to correct it.");

    // ---- named day workers ----

    static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new AppException("Type the day worker's name.");
        if (n.Length > 120) throw new AppException("A name can be at most 120 characters.");
        return n;
    }

    public async Task<Guid> AddWorkerAsync(Guid id, string? name, CancellationToken ct)
    {
        var r = await EditableAsync(id, ct);
        var n = CleanName(name);
        if (await db.SequflohDayWorkers.AnyAsync(w => w.TrackingRecordId == id && w.Name.ToLower() == n.ToLower(), ct)) throw new ConflictException($"{n} is already on this work order.");
        var w = new SequflohDayWorker { TrackingRecordId = id, TrackingNumber = r.Number, Name = n };
        db.SequflohDayWorkers.Add(w);
        Audit(r, "DayWorkerAdded", $"Day worker {n} added to {r.Number}", null, n);
        await db.SaveChangesAsync(ct);
        return w.Id;
    }

    public async Task RenameWorkerAsync(Guid workerId, string? name, CancellationToken ct)
    {
        var w = await db.SequflohDayWorkers.FirstOrDefaultAsync(x => x.Id == workerId, ct) ?? throw new NotFoundException("Day worker");
        var r = await EditableAsync(w.TrackingRecordId, ct);
        var n = CleanName(name);
        if (n == w.Name) return;
        if (await db.SequflohDayWorkers.AnyAsync(x => x.TrackingRecordId == w.TrackingRecordId && x.Id != w.Id && x.Name.ToLower() == n.ToLower(), ct)) throw new ConflictException($"{n} is already on this work order.");
        var before = w.Name; w.Name = n;
        foreach (var s in await db.SequflohCrewSessions.Where(s => s.WorkerId == w.Id).ToListAsync(ct)) s.WorkerName = n;
        Audit(r, "DayWorkerRenamed", $"Day worker on {r.Number} renamed from {before} to {n}", before, n);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a day worker and their time from the work order. The removed times are kept in the audit trail.</summary>
    public async Task RemoveWorkerAsync(Guid workerId, CancellationToken ct)
    {
        var w = await db.SequflohDayWorkers.FirstOrDefaultAsync(x => x.Id == workerId, ct) ?? throw new NotFoundException("Day worker");
        var r = await EditableAsync(w.TrackingRecordId, ct);
        var z = await m.ZoneAsync(r.SiteId, ct);
        if (await db.SequflohCrewSessions.AnyAsync(s => s.WorkerId == w.Id, ct))
            throw new ConflictException($"{w.Name} has recorded time, so they can't be removed. Edit the times to correct them.");
        db.SequflohDayWorkers.Remove(w);
        Audit(r, "DayWorkerRemoved", $"Day worker {w.Name} removed from {r.Number}", w.Name, null);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The working days a bulk entry covers: the chosen days, or every day in the range with weekends skipped by default.</summary>
    public static List<DateOnly> DaysOf(CrewBulkInput x)
    {
        if (x.Days is { Count: > 0 } d) return [.. d.Distinct().Order()];
        if (x.To < x.From) throw new AppException("The end date must be on or after the start date.");
        var list = new List<DateOnly>();
        for (var day = x.From; day <= x.To; day = day.AddDays(1))
            if (!x.SkipWeekends || day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) list.Add(day);
        return list;
    }

    /// <summary>Builds the sessions for a bulk entry: each working day starts at the start time and runs the hours per day, plus the lunch pause.</summary>
    public static List<CrewSessionInput> Plan(CrewBulkInput x, TimeZoneInfo zone)
    {
        CheckCrew(x.CrewSize);
        if (x.HoursPerDay <= 0 || x.HoursPerDay > 16) throw new AppException("Hours per day must be more than 0 and at most 16.");
        if (x.LunchMinutes < 0 || x.LunchMinutes > 240) throw new AppException("Lunch must be between 0 and 240 minutes.");
        var start = TimeOnly.TryParse(x.StartTime ?? "07:00", CultureInfo.InvariantCulture, out var t) ? t : throw new AppException("Enter the start time as HH:mm.");
        var days = DaysOf(x);
        if (days.Count == 0) throw new AppException("There are no working days in that range.");
        if (days.Count > 366) throw new AppException("Bulk entry covers at most 366 days.");
        var list = new List<CrewSessionInput>();
        foreach (var d in days)
        {
            var local = d.ToDateTime(start);
            var s = new DateTimeOffset(local, zone.GetUtcOffset(local));
            var worked = TimeSpan.FromHours((double)x.HoursPerDay);
            var lunch = TimeSpan.FromMinutes(x.LunchMinutes);
            var stop = s + worked + lunch;
            List<CrewPause>? pauses = null;
            if (x.LunchMinutes > 0) { var mid = s + worked / 2; pauses = [new(mid, mid + lunch)]; }
            list.Add(new CrewSessionInput(x.CrewSize, s, stop, pauses, x.Note));
        }
        return list;
    }

    public async Task<object> BulkAsync(Guid id, CrewBulkInput x, bool preview, CancellationToken ct)
    {
        var r = await EditableAsync(id, ct);
        var zone = await m.ZoneAsync(r.SiteId, ct);
        var plan = Plan(x, zone);
        var now = m.Now;
        var made = new List<SequflohCrewSession>();
        foreach (var p in plan)
        {
            var s = new SequflohCrewSession { TrackingRecordId = id, TrackingNumber = r.Number };
            Apply(s, p, now);
            if (made.Any(o => o.StartedAt < s.StoppedAt && o.StoppedAt > s.StartedAt)) throw new AppException("Two of the days overlap.");
            await CheckOverlapAsync(id, null, s.StartedAt, s.StoppedAt, now, ct);
            made.Add(s);
        }
        var labor = made.Sum(s => Labor(s, now));
        var result = new { Days = made.Count, LaborSeconds = labor, LaborHours = H(labor), Sessions = made.Select(s => new { s.StartedAt, s.StoppedAt, s.CrewSize, Seconds = Worked(s, now) }) };
        if (preview) return result;
        db.SequflohCrewSessions.AddRange(made);
        Audit(r, "CrewTimeBulkAdded", $"Crew time added on {r.Number} for {made.Count} working days ({plan[0].Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {plan[^1].Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}): " +
            $"{x.CrewSize} workers x {x.HoursPerDay.ToString("0.##", CultureInfo.InvariantCulture)} h/day = {H(labor)} labor hours", null, string.Join("; ", made.Select(s => Describe(s, zone))));
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ---- summary ----

/// <summary>Crew labor includes the named day workers' time; DayWorkerSeconds is that part on its own.</summary>
    public sealed record Totals(long CrewWorkedSeconds, long CrewPausedSeconds, long CrewLaborSeconds, long UserSeconds, int Users, int MaxCrew, int Sessions, DateOnly? From, DateOnly? To,
        long DayWorkerSeconds = 0, int DayWorkers = 0)
    {
        public long TotalSeconds => CrewLaborSeconds + UserSeconds;
        public int Personnel => MaxCrew + DayWorkers + Users;
    }

    /// <summary>Crew and registered-user totals for a work order. Personnel is the largest crew plus each distinct registered person who clocked on.</summary>
    public async Task<Totals> TotalsAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var now = m.Now;
        var crew = await db.SequflohCrewSessions.AsNoTracking().Where(x => x.TrackingRecordId == id).ToListAsync(ct);
        var time = await db.SequflohTimeEntries.AsNoTracking().Where(x => x.TrackingRecordId == id).ToListAsync(ct);
        var zone = await m.ZoneAsync(r.SiteId, ct);
        var starts = crew.Select(c => c.StartedAt).Concat(time.Select(t => t.StartedAt)).ToList();
        var ends = crew.Select(c => c.StoppedAt ?? now).Concat(time.Select(t => t.StoppedAt ?? now)).ToList();
        DateOnly? D(DateTimeOffset? d) => d is { } v ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(v, zone).DateTime) : null;
        return new Totals(crew.Sum(c => Worked(c, now)), crew.Sum(c => Paused(c, now)), crew.Sum(c => Labor(c, now)), time.Sum(t => SequflohTimeService.Worked(t, now)),
            time.Select(t => t.UserId).Distinct().Count(), crew.Where(c => c.WorkerId == null).Select(c => c.CrewSize).DefaultIfEmpty(0).Max(), crew.Count,
            D(starts.Count == 0 ? null : starts.Min()), D(ends.Count == 0 ? null : ends.Max()),
            crew.Where(c => c.WorkerId != null).Sum(c => Labor(c, now)), crew.Where(c => c.WorkerId != null).Select(c => c.WorkerId).Distinct().Count());
    }

    public async Task<object> SummaryAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var now = m.Now;
        var t = await TotalsAsync(id, ct);
        var list = await db.SequflohCrewSessions.AsNoTracking().Where(x => x.TrackingRecordId == id).OrderByDescending(x => x.StartedAt).ToListAsync(ct);
        var running = list.FirstOrDefault(x => x.StoppedAt is null && x.WorkerId == null);
        var workers = await db.SequflohDayWorkers.AsNoTracking().Where(w => w.TrackingRecordId == id).OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return new
        {
            ServerTime = now,
            CanEdit = !SequflohTrackingService.IsClosed(r) && await m.CanAsync(Permissions.SequflohManageCrewTime, r.SiteId, r.DepartmentId, ct),
            CanClockOff = await m.CanAsync(Permissions.SequflohManageCrewTime, r.SiteId, r.DepartmentId, ct),
            Running = running is null ? null : new { running.Id, running.CrewSize, running.StartedAt, running.PausedAt, Seconds = Worked(running, now), PausedSeconds = Paused(running, now) },
            t.CrewWorkedSeconds, t.CrewPausedSeconds, t.CrewLaborSeconds, t.UserSeconds, t.Users, t.TotalSeconds, t.Personnel, t.MaxCrew, t.From, t.To, t.DayWorkerSeconds,
            Workers = workers.Select(w =>
            {
                var mine = list.Where(s => s.WorkerId == w.Id).ToList();
                var run = mine.FirstOrDefault(s => s.StoppedAt is null);
                return new { w.Id, w.Name, Seconds = mine.Sum(s => Worked(s, now)), Running = run is null ? null : new { run.Id, run.StartedAt, run.PausedAt, Seconds = Worked(run, now), PausedSeconds = Paused(run, now) } };
            }),
            Sessions = list.Select(s => new
            {
                s.Id, s.CrewSize, s.WorkerId, s.WorkerName, s.StartedAt, s.StoppedAt, s.Entered, s.Note, ElapsedSeconds = Elapsed(s, now), PausedSeconds = Paused(s, now), WorkedSeconds = Worked(s, now), LaborSeconds = Labor(s, now),
                Pauses = Pairs(EventsOf(s)),
            }),
        };
    }

    public static List<CrewPause> Pairs(List<CrewEvent> ev)
    {
        var list = new List<CrewPause>();
        DateTimeOffset? open = null;
        foreach (var e in ev)
        {
            if (e.Kind == "Pause") open = e.At;
            else if (e.Kind == "Continue" && open is { } o) { list.Add(new(o, e.At)); open = null; }
        }
        return list;
    }
}
