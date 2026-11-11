using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tracking;

/// <summary>
/// The shop-day clock: arrival, departure, indirect tasks, breaks and the 4:00 PM close.
/// Work-order time stays on <see cref="SequflohTimeService"/>. This service keeps the day in step with it.
/// A sweep is safe to run again: it only closes a day that is still open and past its deadline, and it writes the deadline
/// rather than the moment the sweep happens, so a restart cannot double-count or move the end time.
/// </summary>
public sealed class SequflohDayClockService(AppDbContext db, ICurrentUser me, IAccessService access, SequflohFoundationService foundation)
{
    public const string ListKey = SequflohCatalog.IndirectTasks;
    Guid Me => me.UserId ?? throw new ForbiddenException();

    Task<DateTimeOffset> NowAsync(CancellationToken ct) => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    static string Stamp(DateTimeOffset at) => DayClockTime.Stamp(at);

    void Audit(string action, Guid id, string description, string? reason = null) =>
        foundation.Audit(action, "SequflohDayClock", id, description, reason: reason);

    async Task LockAsync(Guid userId, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", ["clock:" + userId], ct);

    async Task<DayClockSettings> SettingsAsync(CancellationToken ct)
    {
        var json = await db.SequflohSettings.AsNoTracking().Select(s => s.DayClock).FirstOrDefaultAsync(ct);
        var s = SequflohJson.Read<DayClockSettings>(json);
        if (!DayClockTime.TryParse(s.EndTime, out var end)) end = new TimeOnly(16, 0);
        s.EndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        s.HardCapMinutes = Math.Clamp(s.HardCapMinutes, 0, 12 * 60);
        s.WarnMinutes = Math.Clamp(s.WarnMinutes, 0, 180);
        return s;
    }

    TimeOnly EndOf(DayClockSettings s) => DayClockTime.TryParse(s.EndTime, out var end) ? end : new TimeOnly(16, 0);

    async Task<(Guid Id, string Name, Guid? DepartmentId)> WhoAsync(Guid userId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.Id, x.DisplayName, x.DepartmentId }).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Person");
        var dept = u.DepartmentId ?? await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == u.Id && a.DepartmentId != null).Select(a => a.DepartmentId).FirstOrDefaultAsync(ct);
        return (u.Id, u.DisplayName, dept);
    }

    SequflohDayClock? LocalOpen(Guid userId) => db.SequflohDayClocks.Local.FirstOrDefault(c => c.UserId == userId && c.EndedAt == null);

    async Task<SequflohDayClock?> OpenClockAsync(Guid userId, CancellationToken ct) =>
        LocalOpen(userId) ?? await db.SequflohDayClocks.FirstOrDefaultAsync(c => c.UserId == userId && c.EndedAt == null, ct);

    async Task<SequflohDaySegment?> OpenSegmentAsync(Guid userId, CancellationToken ct)
    {
        var local = db.SequflohDaySegments.Local.FirstOrDefault(s => s.UserId == userId && s.EndedAt == null && s.VoidedAt == null);
        return local ?? await db.SequflohDaySegments.FirstOrDefaultAsync(s => s.UserId == userId && s.EndedAt == null, ct);
    }

    public Task<bool> IsInAsync(Guid userId, CancellationToken ct) => db.SequflohDayClocks.AsNoTracking().AnyAsync(c => c.UserId == userId && c.EndedAt == null, ct);

    SequflohDayClock AddClock((Guid Id, string Name, Guid? DepartmentId) who, DateTimeOffset at)
    {
        var clock = new SequflohDayClock { UserId = who.Id, UserName = who.Name, DepartmentId = who.DepartmentId, StartedAt = at };
        db.SequflohDayClocks.Add(clock);
        return clock;
    }

    static void Close(SequflohDaySegment s, DateTimeOffset at, string kind)
    {
        if (s.EndedAt is not null) return;
        if (at < s.StartedAt) at = s.StartedAt;
        s.EndedAt = at;
        s.EndKind = kind;
        s.Seconds = Math.Max(0, (long)(at - s.StartedAt).TotalSeconds);
    }

    SequflohDaySegment Begin(SequflohDayClock clock, string kind, DateTimeOffset at, string? name)
    {
        var s = new SequflohDaySegment { DayClockId = clock.Id, UserId = clock.UserId, UserName = clock.UserName, Kind = kind, StartedAt = at, TaskName = name };
        db.SequflohDaySegments.Add(s);
        return s;
    }

    string? leftBehind;

    // ------------------------------------------------------------------ sweep
    /// <summary>Closes every day that has reached its end. Pass <paramref name="now"/> from a test; otherwise the database clock is used.</summary>
    public async Task<int> SweepAsync(DateTimeOffset? now, CancellationToken ct)
    {
        var at = now ?? await NowAsync(ct);
        var settings = await SettingsAsync(ct);
        var end = EndOf(settings);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", ["day-clock-sweep"], ct);
        var open = await db.SequflohDayClocks.Where(c => c.EndedAt == null).ToListAsync(ct);
        var closed = 0;
        foreach (var clock in open)
        {
            var deadline = DayClockTime.Deadline(clock.StartedAt, clock.WorkingLate, end, settings.HardCapMinutes);
            if (at < deadline) continue;
            var kind = clock.WorkingLate ? "HardCap" : "Automatic";
            var jobs = await db.SequflohTimeEntries.Where(e => e.UserId == clock.UserId && e.StoppedAt == null).ToListAsync(ct);
            foreach (var job in jobs) SequflohTimeService.Finish(job, deadline);
            var segments = await db.SequflohDaySegments.Where(s => s.UserId == clock.UserId && s.EndedAt == null).ToListAsync(ct);
            foreach (var s in segments) Close(s, deadline, kind);
            clock.EndedAt = deadline;
            clock.EndKind = kind;
            var why = kind == "HardCap"
                ? $"{clock.UserName} was clocked out at {Stamp(deadline)}, the latest a Working late day can run. Any task still running was stopped first."
                : $"{clock.UserName} was clocked out automatically at {Stamp(deadline)} (end of the day). Any task still running was stopped first.";
            Audit(kind == "HardCap" ? "DayClockHardCap" : "DayClockAutomatic", clock.Id, why);
            closed++;
        }
        if (closed > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return closed;
    }

    // ------------------------------------------------------------------ clock in, out, late
    public async Task<object> ClockInAsync(CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var at = await NowAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(Me, ct);
        if (await OpenClockAsync(Me, ct) is not null) throw new ConflictException("You are already clocked in for the day.");
        var who = await WhoAsync(Me, ct);
        var clock = AddClock(who, at);
        Audit("DayClockIn", clock.Id, $"{who.Name} clocked in for the day at {Stamp(at)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    public async Task<object> ClockOutAsync(CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var at = await NowAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(Me, ct);
        var clock = await OpenClockAsync(Me, ct) ?? throw new ConflictException("You are not clocked in for the day.");
        await FinishJobsAsync(clock.UserId, at, ct);
        var open = await OpenSegmentAsync(clock.UserId, ct);
        if (open is not null) Close(open, at, "ClockOut");
        clock.EndedAt = at;
        clock.EndKind = "Manual";
        Audit("DayClockOut", clock.Id, $"{clock.UserName} clocked out at {Stamp(at)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    public async Task<object> WorkLateAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        if (settings.HardCapMinutes <= 0) throw new AppException("Working late is switched off. The day ends at the scheduled time.");
        var at = await NowAsync(ct);
        var end = EndOf(settings);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", ["day-clock-sweep"], ct);
        await LockAsync(Me, ct);
        var clock = await OpenClockAsync(Me, ct) ?? throw new ConflictException("You are not clocked in for the day.");
        var hard = DayClockTime.Deadline(clock.StartedAt, true, end, settings.HardCapMinutes);
        if (at >= hard)
        {
            await tx.CommitAsync(ct);
            await SweepAsync(at, ct);
            throw new ConflictException("The day is already past the latest clock-out.");
        }
        clock.WorkingLate = true;
        Audit("DayClockLate", clock.Id, $"{clock.UserName} chose Working late. The day stays open until they clock out or {Stamp(hard)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    async Task FinishJobsAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        var jobs = await db.SequflohTimeEntries.Where(e => e.UserId == userId && e.StoppedAt == null).ToListAsync(ct);
        foreach (var job in jobs) SequflohTimeService.Finish(job, at);
    }

    // ------------------------------------------------------------------ called by the work-order clock
    public async Task PrepareJobStartAsync(bool clockIn, DateTimeOffset at, CancellationToken ct)
    {
        leftBehind = null;
        var who = await WhoAsync(Me, ct);
        var clock = await OpenClockAsync(who.Id, ct);
        if (clock is null)
        {
            if (!clockIn) throw new ConflictException(SequflohTimeService.ClockInFirstMessage);
            clock = AddClock(who, at);
            Audit("DayClockIn", clock.Id, $"{who.Name} clocked in for the day at {Stamp(at)}, and started a job.");
        }
        var open = await OpenSegmentAsync(who.Id, ct);
        if (open is not null && open.Kind is not "WorkOrder")
        {
            leftBehind = open.TaskName ?? (open.Kind == "Lunch" ? "lunch" : "a break");
            Close(open, at, "Switch");
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task AddJobSegmentAsync(SequflohTrackingRecord rec, SequflohTimeEntry entry, DateTimeOffset at, CancellationToken ct)
    {
        var clock = await OpenClockAsync(entry.UserId, ct) ?? throw new ConflictException(SequflohTimeService.ClockInFirstMessage);
        var open = await OpenSegmentAsync(entry.UserId, ct);
        if (open is not null) { Close(open, at, "Switch"); await db.SaveChangesAsync(ct); }
        var seg = Begin(clock, "WorkOrder", at, rec.Number);
        seg.TrackingRecordId = rec.Id;
        seg.TrackingNumber = rec.Number;
        seg.TimeEntryId = entry.Id;
        var sentence = leftBehind is null
            ? $"{entry.UserName} started work order {rec.Number} at {Stamp(at)}."
            : $"{entry.UserName} switched from {leftBehind} to work order {rec.Number} at {Stamp(at)}.";
        leftBehind = null;
        Audit("DayClockJob", clock.Id, sentence);
    }

    public async Task RecordJobPauseAsync(SequflohTimeEntry e, string reason, DateTimeOffset at, CancellationToken ct)
    {
        var clock = await OpenClockAsync(e.UserId, ct);
        if (clock is null) return;
        var open = await OpenSegmentAsync(e.UserId, ct);
        if (open?.Kind is "Break" or "Lunch") return;
        if (open is not null) { Close(open, at, "Pause"); await db.SaveChangesAsync(ct); }
        var kind = reason.Equals("Lunch", StringComparison.OrdinalIgnoreCase) ? "Lunch" : "Break";
        var seg = Begin(clock, kind, at, kind);
        seg.ResumeKind = "WorkOrder";
        seg.ResumeTrackingRecordId = e.TrackingRecordId;
        seg.ResumeTrackingNumber = e.TrackingNumber;
        seg.ResumeTimeEntryId = e.Id;
        seg.ResumeTaskName = e.TrackingNumber;
        Audit("DayClockPause", clock.Id, $"{e.UserName} paused work order {e.TrackingNumber} for {kind.ToLowerInvariant()} at {Stamp(at)}.");
    }

    public async Task RecordJobResumeAsync(SequflohTimeEntry e, DateTimeOffset at, CancellationToken ct)
    {
        var clock = await OpenClockAsync(e.UserId, ct);
        if (clock is null) return;
        var open = await OpenSegmentAsync(e.UserId, ct);
        if (open?.Kind is "Break" or "Lunch") { Close(open, at, "Resume"); await db.SaveChangesAsync(ct); }
        else if (open is not null) return;
        var seg = Begin(clock, "WorkOrder", at, e.TrackingNumber);
        seg.TrackingRecordId = e.TrackingRecordId;
        seg.TrackingNumber = e.TrackingNumber;
        seg.TimeEntryId = e.Id;
        Audit("DayClockResume", clock.Id, $"{e.UserName} resumed work order {e.TrackingNumber} at {Stamp(at)}.");
    }

    public async Task RecordJobStopAsync(SequflohTimeEntry e, DateTimeOffset at, CancellationToken ct)
    {
        var clock = await OpenClockAsync(e.UserId, ct);
        var open = await OpenSegmentAsync(e.UserId, ct);
        if (open is null) return;
        var mine = open.Kind == "WorkOrder" && (open.TimeEntryId is null || open.TimeEntryId == e.Id)
            || open.Kind is "Break" or "Lunch" && open.ResumeTimeEntryId == e.Id;
        if (!mine) return;
        Close(open, at, "Stop");
        if (clock is not null) Audit("DayClockJobStop", clock.Id, $"{e.UserName} clocked off work order {e.TrackingNumber} at {Stamp(at)} and stayed clocked in for the day.");
    }

    // ------------------------------------------------------------------ indirect, pause, resume from the day clock
    public async Task<object> StartIndirectAsync(Guid taskId, string? note, bool clockIn, CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var task = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == taskId && v.ListKey == ListKey && v.IsActive, ct)
            ?? throw new AppException("Choose an active indirect task.");
        var clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (clean is { Length: > 300 }) throw new AppException("The note is limited to 300 characters.");
        var at = await NowAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(Me, ct);
        var who = await WhoAsync(Me, ct);
        var clock = await OpenClockAsync(who.Id, ct);
        if (clock is null)
        {
            if (!clockIn) throw new ConflictException(SequflohTimeService.ClockInFirstMessage);
            clock = AddClock(who, at);
            Audit("DayClockIn", clock.Id, $"{who.Name} clocked in for the day at {Stamp(at)}, and started {task.Label}.");
        }
        await FinishJobsAsync(who.Id, at, ct);
        var open = await OpenSegmentAsync(who.Id, ct);
        if (open is { Kind: "Indirect", IndirectValueId: var same } && same == task.Id)
        {
            if (clean is not null) open.Note = clean;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await StatusAsync(ct, sweep: false);
        }
        var from = open is null ? null : open.TaskName ?? open.Kind;
        if (open is not null) { Close(open, at, "Switch"); await db.SaveChangesAsync(ct); }
        var seg = Begin(clock, "Indirect", at, task.Label);
        seg.IndirectValueId = task.Id;
        seg.Note = clean;
        var sentence = from is null
            ? $"{who.Name} started {task.Label} at {Stamp(at)}{(clean is null ? "" : $" ({clean})")}."
            : $"{who.Name} switched from {from} to {task.Label} at {Stamp(at)}{(clean is null ? "" : $" ({clean})")}.";
        Audit("DayClockIndirect", clock.Id, sentence);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    public async Task<object> PauseAsync(string? reason, CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var why = SequflohTimeService.PauseReasons.FirstOrDefault(r => string.Equals(r, reason?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("Choose why you are pausing: Break or Lunch.");
        var at = await NowAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(Me, ct);
        var clock = await OpenClockAsync(Me, ct) ?? throw new ConflictException("Clock in for the day before taking a break.");
        var live = await db.SequflohTimeEntries.Where(e => e.UserId == Me && e.StoppedAt == null && e.PausedAt == null).ToListAsync(ct);
        if (live.Count > 0)
        {
            foreach (var job in live) SequflohTimeService.ApplyPause(job, why, at);
            await RecordJobPauseAsync(live[0], why, at, ct);
        }
        else
        {
            var open = await OpenSegmentAsync(Me, ct);
            if (open?.Kind is "Break" or "Lunch") throw new ConflictException("You are already on a break.");
            string? resumeKind = null, resumeName = null, resumeNote = null, resumeNumber = null;
            Guid? resumeTask = null, resumeRecord = null, resumeEntry = null;
            if (open is not null)
            {
                resumeKind = open.Kind;
                resumeName = open.TaskName;
                resumeNote = open.Note;
                resumeNumber = open.TrackingNumber;
                resumeTask = open.IndirectValueId;
                resumeRecord = open.TrackingRecordId;
                resumeEntry = open.TimeEntryId;
                Close(open, at, "Pause");
                await db.SaveChangesAsync(ct);
            }
            var kind = why.Equals("Lunch", StringComparison.OrdinalIgnoreCase) ? "Lunch" : "Break";
            var seg = Begin(clock, kind, at, kind);
            seg.ResumeKind = resumeKind;
            seg.ResumeTaskName = resumeName;
            seg.ResumeNote = resumeNote;
            seg.ResumeTrackingNumber = resumeNumber;
            seg.ResumeIndirectValueId = resumeTask;
            seg.ResumeTrackingRecordId = resumeRecord;
            seg.ResumeTimeEntryId = resumeEntry;
            var what = resumeName is null ? "the day" : resumeName;
            Audit("DayClockPause", clock.Id, $"{clock.UserName} paused {what} for {kind.ToLowerInvariant()} at {Stamp(at)}.");
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    public async Task<object> ResumeAsync(CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var at = await NowAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(Me, ct);
        var clock = await OpenClockAsync(Me, ct) ?? throw new ConflictException("You are not clocked in for the day.");
        var paused = await db.SequflohTimeEntries.Where(e => e.UserId == Me && e.StoppedAt == null && e.PausedAt != null).ToListAsync(ct);
        if (paused.Count > 0)
        {
            var rec = await db.SequflohTrackingRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == paused[0].TrackingRecordId, ct);
            if (rec is not null) SequflohTrackingService.RequireOpen(rec);
            foreach (var job in paused) SequflohTimeService.ResumeEntry(job, at);
            await RecordJobResumeAsync(paused[0], at, ct);
        }
        else
        {
            var open = await OpenSegmentAsync(Me, ct);
            if (open?.Kind is not ("Break" or "Lunch")) throw new ConflictException("You are not on a break.");
            var resumeKind = open.ResumeKind;
            var resumeName = open.ResumeTaskName;
            var resumeNote = open.ResumeNote;
            var resumeTask = open.ResumeIndirectValueId;
            var resumeNumber = open.ResumeTrackingNumber;
            Close(open, at, "Resume");
            await db.SaveChangesAsync(ct);
            if (resumeKind == "Indirect" && resumeTask is { } taskId)
            {
                var seg = Begin(clock, "Indirect", at, resumeName);
                seg.IndirectValueId = taskId;
                seg.Note = resumeNote;
                Audit("DayClockResume", clock.Id, $"{clock.UserName} resumed {resumeName} at {Stamp(at)}.");
            }
            else if (resumeKind == "WorkOrder")
                Audit("DayClockResume", clock.Id, $"{clock.UserName} ended the break at {Stamp(at)}. Work order {resumeNumber} was already clocked off, so the day is open with nothing running.");
            else
                Audit("DayClockResume", clock.Id, $"{clock.UserName} ended the break at {Stamp(at)}.");
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await StatusAsync(ct, sweep: false);
    }

    // ------------------------------------------------------------------ read models
    public async Task<object> StatusAsync(CancellationToken ct, bool sweep = true)
    {
        if (sweep) await SweepAsync(null, ct);
        var settings = await SettingsAsync(ct);
        var end = EndOf(settings);
        var now = await NowAsync(ct);
        var clock = await db.SequflohDayClocks.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == Me && c.EndedAt == null, ct);
        var open = clock is null ? null : await db.SequflohDaySegments.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == Me && s.EndedAt == null, ct);
        var tasks = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == ListKey && v.IsActive).OrderBy(v => v.SortOrder).ThenBy(v => v.Label)
            .Select(v => new { v.Id, v.Label }).ToListAsync(ct);
        DateTimeOffset? scheduled = null, hard = null;
        var offer = false;
        if (clock is not null)
        {
            scheduled = DayClockTime.Deadline(clock.StartedAt, false, end, settings.HardCapMinutes);
            hard = DayClockTime.Deadline(clock.StartedAt, true, end, settings.HardCapMinutes);
            offer = !clock.WorkingLate && settings.HardCapMinutes > 0 && now >= scheduled.Value.AddMinutes(-settings.WarnMinutes) && now < hard.Value;
        }
        var can = new
        {
            Own = await access.HasPermissionAsync(Me, Permissions.SequflohViewOwnDayClock, ct),
            Team = await access.HasPermissionAsync(Me, Permissions.SequflohViewTeamDayClock, ct),
            All = await access.HasPermissionAsync(Me, Permissions.SequflohViewAllDayClock, ct),
            Correct = await access.HasPermissionAsync(Me, Permissions.SequflohCorrectDayClock, ct),
            Manage = await access.HasPermissionAsync(Me, Permissions.SequflohManageDayClock, ct),
        };
        return new
        {
            ClockedIn = clock is not null,
            StartedAt = clock?.StartedAt,
            WorkingLate = clock?.WorkingLate == true,
            ScheduledEnd = scheduled,
            HardCapAt = hard,
            OfferLate = offer,
            EndLabel = DayClockTime.Clock(end),
            ServerTime = now,
            Activity = open is null ? null : new
            {
                open.Kind,
                Name = open.TaskName ?? open.Kind,
                Since = open.StartedAt,
                open.Note,
                open.TrackingRecordId,
                TaskId = open.IndirectValueId,
                ResumeName = open.ResumeTaskName,
            },
            Tasks = tasks,
            Can = can,
            Reports = await db.Users.AsNoTracking().Where(u => u.ManagerId == Me && u.IsActive).OrderBy(u => u.DisplayName)
                .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct),
        };
    }

    async Task<string> ScopeAsync(CancellationToken ct)
    {
        if (await access.HasPermissionAsync(Me, Permissions.SequflohViewAllDayClock, ct)) return "all";
        if (await access.HasPermissionAsync(Me, Permissions.SequflohViewTeamDayClock, ct)) return "team";
        if (await access.HasPermissionAsync(Me, Permissions.SequflohViewOwnDayClock, ct)) return "own";
        return "none";
    }

    async Task<HashSet<Guid>> DepartmentsOfAsync(Guid userId, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        var home = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        if (home is { } d) set.Add(d);
        foreach (var id in await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == userId && a.DepartmentId != null).Select(a => a.DepartmentId!.Value).ToListAsync(ct))
            set.Add(id);
        return set;
    }

    async Task EnsureCanSeeAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Me)
        {
            if (await ScopeAsync(ct) == "none") throw new ForbiddenException("You do not have permission to view day-clock timesheets.");
            return;
        }
        if (await IsDirectReportAsync(userId, ct)) return;
        var scope = await ScopeAsync(ct);
        if (scope == "all") return;
        if (scope == "team")
        {
            var mine = await DepartmentsOfAsync(Me, ct);
            var theirs = await DepartmentsOfAsync(userId, ct);
            if (mine.Overlaps(theirs)) return;
        }
        throw new ForbiddenException("You do not have permission to view this person's day clock.");
    }

    /// <summary>The signed-in person is this user's assigned manager. Direct reports only; the line does not walk upward.</summary>
    Task<bool> IsDirectReportAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.ManagerId == Me, ct);

    async Task<List<(Guid Id, string Name)>> DirectReportsAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.ManagerId == Me && u.IsActive).OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct))
        .Select(u => (u.Id, u.DisplayName)).ToList();

    public async Task<object> TimesheetAsync(Guid? userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var id = userId ?? Me;
        await EnsureCanSeeAsync(id, ct);
        var today = DayClockTime.LocalDate(await NowAsync(ct));
        var start = from ?? today;
        var end = to ?? start;
        if (end < start) throw new AppException("The start date must be on or before the end date.");
        if (end.DayNumber - start.DayNumber > 62) throw new AppException("Pick a range of 62 days or fewer.");
        var who = await WhoAsync(id, ct);
        var sheet = await BuildAsync(id, who.Name, start, end, await NowAsync(ct), ct);
        return sheet;
    }

    public async Task<object> TeamAsync(CancellationToken ct)
    {
        await SweepAsync(null, ct);
        var scope = await ScopeAsync(ct);
        var reports = await DirectReportsAsync(ct);
        if (scope is not ("team" or "all") && reports.Count == 0) throw new ForbiddenException("You do not have permission to view the team day clock.");
        var now = await NowAsync(ct);
        var clocks = await db.SequflohDayClocks.AsNoTracking().Where(c => c.EndedAt == null).OrderBy(c => c.UserName).ToListAsync(ct);
        if (scope is not ("team" or "all"))
            clocks = clocks.Where(c => reports.Any(r => r.Id == c.UserId)).ToList();
        HashSet<Guid>? mine = null;
        if (scope == "team")
        {
            mine = await DepartmentsOfAsync(Me, ct);
            var homes = await db.Users.AsNoTracking().Where(u => clocks.Select(c => c.UserId).Contains(u.Id)).Select(u => new { u.Id, u.DepartmentId }).ToListAsync(ct);
            bool Keep(SequflohDayClock c)
            {
                if (c.UserId == Me) return true;
                if (c.DepartmentId is { } d && mine.Contains(d)) return true;
                var home = homes.FirstOrDefault(h => h.Id == c.UserId)?.DepartmentId;
                return home is { } h && mine.Contains(h);
            }
            clocks = clocks.Where(Keep).ToList();
        }
        var ids = clocks.Select(c => c.UserId).ToList();
        var segments = await db.SequflohDaySegments.AsNoTracking().Where(s => ids.Contains(s.UserId) && s.EndedAt == null).ToListAsync(ct);
        var deptIds = clocks.Select(c => c.DepartmentId).Where(d => d is not null).Select(d => d!.Value).Distinct().ToList();
        var deptNames = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var people = clocks.Select(c =>
        {
            var seg = segments.FirstOrDefault(s => s.UserId == c.UserId);
            var activity = seg is null ? "Clocked in, not on a task" : seg.Kind switch
            {
                "WorkOrder" => seg.TrackingNumber ?? seg.TaskName ?? "Work order",
                "Indirect" => seg.TaskName ?? "Indirect task",
                "Break" => "Break",
                "Lunch" => "Lunch",
                _ => seg.TaskName ?? seg.Kind,
            };
            return new
            {
                c.UserId,
                Name = c.UserName,
                Department = c.DepartmentId is { } d && deptNames.TryGetValue(d, out var n) ? n : null,
                Since = c.StartedAt,
                Activity = activity,
                ActivitySince = seg?.StartedAt ?? c.StartedAt,
                Kind = seg?.Kind ?? "Idle",
                c.WorkingLate,
            };
        }).ToList();
        var reportIds = reports.Select(r => r.Id).ToList();
        var reportClocks = await db.SequflohDayClocks.AsNoTracking().Where(c => reportIds.Contains(c.UserId) && c.EndedAt == null).ToListAsync(ct);
        var reportSegs = await db.SequflohDaySegments.AsNoTracking().Where(s => reportIds.Contains(s.UserId) && s.EndedAt == null).ToListAsync(ct);
        var mineTeam = reports.Select(r =>
        {
            var clock = reportClocks.FirstOrDefault(c => c.UserId == r.Id);
            var seg = reportSegs.FirstOrDefault(s => s.UserId == r.Id);
            var activity = clock is null ? "Not clocked in" : seg is null ? "Clocked in, not on a task" : seg.Kind switch
            {
                "WorkOrder" => seg.TrackingNumber ?? seg.TaskName ?? "Work order",
                "Indirect" => seg.TaskName ?? "Indirect task",
                "Break" => "Break",
                "Lunch" => "Lunch",
                _ => seg.TaskName ?? seg.Kind,
            };
            return new { UserId = r.Id, Name = r.Name, ClockedIn = clock is not null, Activity = activity, Since = clock?.StartedAt };
        }).ToList();
        return new { ServerTime = now, People = people, Reports = mineTeam };
    }

    public async Task<string> CsvAsync(Guid? userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var settings = await foundation.GetSettingsAsync(ct);
        var scope = await ScopeAsync(ct);
        var managesOne = userId is { } report && await IsDirectReportAsync(report, ct);
        var ownReports = scope == "none" && userId is null ? await DirectReportsAsync(ct) : [];
        if (settings.Security.ExportsRequirePermission && !managesOne && ownReports.Count == 0 && !await access.HasPermissionAsync(Me, Permissions.SequflohExportRecords, ct))
            throw new ForbiddenException("Exporting requires the export permission.");
        if (scope == "none" && !managesOne && userId is not null) throw new ForbiddenException("You do not have permission to view day-clock timesheets.");
        var today = DayClockTime.LocalDate(await NowAsync(ct));
        var start = from ?? today;
        var end = to ?? start;
        if (end < start) throw new AppException("The start date must be on or before the end date.");
        List<Guid> ids;
        if (userId is { } one)
        {
            await EnsureCanSeeAsync(one, ct);
            ids = [one];
        }
        else if (scope == "none")
        {
            ids = ownReports.Select(r => r.Id).ToList();
            if (ids.Count == 0) throw new ForbiddenException("You do not have permission to view day-clock timesheets.");
        }
        else if (scope == "own") ids = [Me];
        else if (scope == "all")
        {
            var (a, b) = DayClockTime.LocalDay(start);
            var last = DayClockTime.LocalDay(end).End;
            ids = await db.SequflohDayClocks.AsNoTracking().Where(c => c.StartedAt < last && (c.EndedAt == null || c.EndedAt > a)).Select(c => c.UserId).Distinct().ToListAsync(ct);
        }
        else
        {
            var mine = await DepartmentsOfAsync(Me, ct);
            var (a, b) = DayClockTime.LocalDay(start);
            _ = b;
            var last = DayClockTime.LocalDay(end).End;
            var rows = await db.SequflohDayClocks.AsNoTracking().Where(c => c.StartedAt < last && (c.EndedAt == null || c.EndedAt > a)).Select(c => new { c.UserId, c.DepartmentId }).ToListAsync(ct);
            ids = rows.Where(r => r.UserId == Me || (r.DepartmentId is { } d && mine.Contains(d))).Select(r => r.UserId).Distinct().ToList();
        }
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = await NowAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("Date,Person,Arrival,Clock out,Clock-out kind,Work order time,Indirect time,Indirect tasks,Break,Lunch,Unaccounted");
        foreach (var id in ids.OrderBy(i => names.GetValueOrDefault(i)))
        {
            var sheet = await BuildAsync(id, names.GetValueOrDefault(id) ?? "", start, end, now, ct);
            foreach (var day in sheet.Days)
            {
                var tasks = string.Join("; ", day.Indirect.Select(t => $"{t.Task} {Fmt(t.Seconds)}"));
                sb.AppendLine(string.Join(",",
                    day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Csv(sheet.UserName),
                    Csv(day.Arrival is { } arr ? Stamp(arr) : ""),
                    Csv(day.ClockOut is { } dep ? Stamp(dep) : ""),
                    Csv(day.ClockOutKind ?? ""),
                    Fmt(day.WorkSeconds), Fmt(day.IndirectSeconds), Csv(tasks),
                    Fmt(day.BreakSeconds), Fmt(day.LunchSeconds), Fmt(day.UnaccountedSeconds)));
            }
        }
        return sb.ToString();
    }

    static string Csv(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    static string Fmt(long seconds)
    {
        var v = Math.Max(0, seconds);
        return $"{v / 3600:00}:{(v % 3600) / 60:00}:{v % 60:00}";
    }

    public sealed record TaskTotal(string Task, long Seconds);
    public sealed record OrderTotal(string Number, long Seconds);
    public sealed record SegmentRow(Guid Id, string Kind, string Name, string? Note, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long Seconds, string? EndKind);
    public sealed record DayRow(DateOnly Date, DateTimeOffset? Arrival, DateTimeOffset? ClockOut, string? ClockOutKind, bool Automatic,
        long WorkSeconds, long IndirectSeconds, long BreakSeconds, long LunchSeconds, long UnaccountedSeconds,
        IReadOnlyList<TaskTotal> Indirect, IReadOnlyList<OrderTotal> WorkOrders, IReadOnlyList<SegmentRow> Segments);
    public sealed record Sheet(Guid UserId, string UserName, DateOnly From, DateOnly To, IReadOnlyList<DayRow> Days,
        long WorkSeconds, long IndirectSeconds, long BreakSeconds, long LunchSeconds, long UnaccountedSeconds);

    async Task<Sheet> BuildAsync(Guid userId, string name, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        var first = DayClockTime.LocalDay(from).Start;
        var last = DayClockTime.LocalDay(to).End;
        var clocks = await db.SequflohDayClocks.AsNoTracking().Where(c => c.UserId == userId && c.StartedAt < last && (c.EndedAt == null || c.EndedAt > first)).ToListAsync(ct);
        var clockIds = clocks.Select(c => c.Id).ToList();
        var segments = await db.SequflohDaySegments.AsNoTracking().Where(s => clockIds.Contains(s.DayClockId)).OrderBy(s => s.StartedAt).ToListAsync(ct);
        var days = new List<DayRow>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var (day0, day1) = DayClockTime.LocalDay(day);
            long work = 0, indirect = 0, brk = 0, lunch = 0, idle = 0;
            var byTask = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var byOrder = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<SegmentRow>();
            DateTimeOffset? arrival = null, departure = null;
            string? kind = null;
            var openDay = false;
            foreach (var clock in clocks)
            {
                var stop = clock.EndedAt ?? now;
                var span = Overlap(clock.StartedAt, stop, day0, day1);
                if (span == 0 && !(clock.StartedAt < day1 && stop > day0)) continue;
                if (DayClockTime.LocalDate(clock.StartedAt) == day && (arrival is null || clock.StartedAt < arrival)) arrival = clock.StartedAt;
                if (clock.EndedAt is null) openDay = true;
                else if (departure is null || clock.EndedAt > departure) { departure = clock.EndedAt; kind = clock.EndKind; }
                long accounted = 0;
                foreach (var s in segments.Where(s => s.DayClockId == clock.Id))
                {
                    var sec = Overlap(s.StartedAt, s.EndedAt ?? now, day0, day1);
                    if (s.StartedAt >= day1 || (s.EndedAt ?? now) <= day0) continue;
                    accounted += sec;
                    switch (s.Kind)
                    {
                        case "WorkOrder":
                            work += sec;
                            var n = s.TrackingNumber ?? s.TaskName ?? "Work order";
                            byOrder[n] = byOrder.GetValueOrDefault(n) + sec;
                            break;
                        case "Indirect":
                            indirect += sec;
                            var t = s.TaskName ?? "Indirect";
                            byTask[t] = byTask.GetValueOrDefault(t) + sec;
                            break;
                        case "Lunch": lunch += sec; break;
                        default: brk += sec; break;
                    }
                    if (s.StartedAt < day1 && (s.EndedAt ?? now) > day0)
                        rows.Add(new SegmentRow(s.Id, s.Kind, s.TaskName ?? s.Kind, s.Note, s.StartedAt, s.EndedAt, sec, s.EndKind));
                }
                idle += Math.Max(0, span - accounted);
            }
            if (openDay) { departure = null; kind = null; }
            days.Add(new DayRow(day, arrival, departure, kind, kind is "Automatic" or "HardCap", work, indirect, brk, lunch, idle,
                byTask.OrderBy(p => p.Key).Select(p => new TaskTotal(p.Key, p.Value)).ToList(),
                byOrder.OrderBy(p => p.Key).Select(p => new OrderTotal(p.Key, p.Value)).ToList(),
                rows.OrderBy(r => r.StartedAt).ToList()));
        }
        return new Sheet(userId, name, from, to, days, days.Sum(d => d.WorkSeconds), days.Sum(d => d.IndirectSeconds), days.Sum(d => d.BreakSeconds), days.Sum(d => d.LunchSeconds), days.Sum(d => d.UnaccountedSeconds));
    }

    static long Overlap(DateTimeOffset a0, DateTimeOffset a1, DateTimeOffset b0, DateTimeOffset b1)
    {
        var s = a0 > b0 ? a0 : b0;
        var e = a1 < b1 ? a1 : b1;
        return e > s ? (long)(e - s).TotalSeconds : 0;
    }

    public async Task<object> HistoryAsync(Guid? userId, CancellationToken ct)
    {
        var id = userId ?? Me;
        if (id != Me) await EnsureCanSeeAsync(id, ct);
        var clockIds = await db.SequflohDayClocks.AsNoTracking().Where(c => c.UserId == id).Select(c => c.Id).ToListAsync(ct);
        var rows = await db.AuditEntries.AsNoTracking().Where(a => a.EntityType == "SequflohDayClock" && clockIds.Contains(a.EntityId))
            .OrderByDescending(a => a.OccurredAt).Take(200)
            .Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Reason }).ToListAsync(ct);
        return new { UserId = id, Entries = rows };
    }

    // ------------------------------------------------------------------ corrections
    public sealed record CorrectionInput(string? Reason, Guid? UserId, string? Action, Guid? DayClockId, Guid? SegmentId,
        DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Kind, Guid? TrackingRecordId, Guid? IndirectValueId, string? Note);

    public async Task<object> CorrectAsync(CorrectionInput input, CancellationToken ct)
    {
        var target = input.UserId ?? Me;
        var manages = target != Me && await IsDirectReportAsync(target, ct);
        if (!manages && !await access.HasPermissionAsync(Me, Permissions.SequflohCorrectDayClock, ct))
            throw new ForbiddenException("You do not have permission to correct the day clock.");
        var reason = input.Reason?.Trim() ?? "";
        if (reason.Length < 3) throw new AppException("Enter a reason for the correction.");
        if (reason.Length > 500) throw new AppException("The reason is limited to 500 characters.");
        var who = await WhoAsync(input.UserId ?? Me, ct);
        var actor = me.UserName ?? "Someone";
        var now = await NowAsync(ct);
        switch (input.Action)
        {
            case "add-clock": await AddMissedClockAsync(who, input, reason, actor, now, ct); break;
            case "adjust-clock": await AdjustClockAsync(who, input, reason, actor, now, ct); break;
            case "add-segment": await AddSegmentAsync(who, input, reason, actor, now, ct); break;
            case "adjust-segment": await AdjustSegmentAsync(who, input, reason, actor, now, ct); break;
            case "void-segment": await VoidSegmentAsync(who, input, reason, actor, now, ct); break;
            default: throw new AppException("Choose what to correct.");
        }
        return await HistoryAsync(who.Id, ct);
    }

    static void NotFuture(DateTimeOffset at, DateTimeOffset now)
    {
        if (at > now.AddMinutes(2)) throw new AppException("A correction cannot be in the future.");
    }

    async Task EnsureClockGapAsync(Guid userId, Guid? except, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var rows = await db.SequflohDayClocks.Where(c => c.UserId == userId && c.Id != except).ToListAsync(ct);
        if (rows.Any(c => c.StartedAt < end && (c.EndedAt ?? DateTimeOffset.MaxValue) > start))
            throw new ConflictException("That time overlaps a day already on the clock.");
    }

    async Task AddMissedClockAsync((Guid Id, string Name, Guid? DepartmentId) who, CorrectionInput input, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (input.StartedAt is not { } start || input.EndedAt is not { } end) throw new AppException("Enter the arrival and the clock-out.");
        if (end <= start) throw new AppException("The clock-out must be after the arrival.");
        NotFuture(start, now); NotFuture(end, now);
        await EnsureClockGapAsync(who.Id, null, start, end, ct);
        var clock = AddClock(who, start);
        clock.EndedAt = end;
        clock.EndKind = "Manual";
        Audit("DayClockCorrected", clock.Id, $"{actor} added a missed day for {who.Name}, in at {Stamp(start)} and out at {Stamp(end)}. Reason: {reason}", reason);
        await db.SaveChangesAsync(ct);
    }

    async Task AdjustClockAsync((Guid Id, string Name, Guid? DepartmentId) who, CorrectionInput input, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var clock = await db.SequflohDayClocks.FirstOrDefaultAsync(c => c.Id == input.DayClockId && c.UserId == who.Id, ct) ?? throw new NotFoundException("Day clock");
        var start = input.StartedAt ?? clock.StartedAt;
        var end = input.EndedAt ?? clock.EndedAt;
        if (end is { } e && e <= start) throw new AppException("The clock-out must be after the arrival.");
        NotFuture(start, now);
        if (end is { } e2) NotFuture(e2, now);
        if (end is null && await db.SequflohDayClocks.AnyAsync(c => c.UserId == who.Id && c.Id != clock.Id && c.EndedAt == null, ct))
            throw new ConflictException("This person is already clocked in on another day.");
        var closeAt = end ?? DateTimeOffset.MaxValue;
        await EnsureClockGapAsync(who.Id, clock.Id, start, closeAt == DateTimeOffset.MaxValue ? start.AddYears(1) : closeAt, ct);
        var segments = await db.SequflohDaySegments.Where(s => s.DayClockId == clock.Id).ToListAsync(ct);
        if (segments.Any(s => s.StartedAt < start || (s.EndedAt ?? end ?? now) > (end ?? DateTimeOffset.MaxValue)))
            throw new AppException("Move or remove the tasks that fall outside the new arrival and clock-out first.");
        var beforeIn = Stamp(clock.StartedAt);
        var beforeOut = clock.EndedAt is { } old ? Stamp(old) : "still clocked in";
        if (clock.EndedAt is null && end is { } closing)
        {
            await FinishJobsAsync(clock.UserId, closing, ct);
            var open = segments.FirstOrDefault(s => s.EndedAt == null);
            if (open is not null) Close(open, closing, "ClockOut");
        }
        clock.StartedAt = start;
        clock.EndedAt = end;
        if (end is not null && clock.EndKind is null) clock.EndKind = "Manual";
        if (end is null) clock.EndKind = null;
        Audit("DayClockCorrected", clock.Id, $"{actor} changed {who.Name}'s day from {beforeIn} / {beforeOut} to {Stamp(start)} / {(end is { } n ? Stamp(n) : "still clocked in")}. Reason: {reason}", reason);
        await db.SaveChangesAsync(ct);
    }

    async Task<SequflohDayClock> ClockForAsync(Guid userId, Guid? clockId, DateTimeOffset start, CancellationToken ct)
    {
        if (clockId is { } id)
            return await db.SequflohDayClocks.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct) ?? throw new NotFoundException("Day clock");
        var clock = await db.SequflohDayClocks.Where(c => c.UserId == userId && c.StartedAt <= start && (c.EndedAt == null || c.EndedAt >= start)).FirstOrDefaultAsync(ct);
        return clock ?? throw new AppException("That time is not inside a clocked-in day. Add the missed day first.");
    }

    async Task AddSegmentAsync((Guid Id, string Name, Guid? DepartmentId) who, CorrectionInput input, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (input.StartedAt is not { } start || input.EndedAt is not { } end) throw new AppException("Enter the start and end of the task.");
        if (end <= start) throw new AppException("The task must end after it starts.");
        NotFuture(start, now); NotFuture(end, now);
        var kind = input.Kind ?? "";
        if (kind is not ("WorkOrder" or "Indirect" or "Break" or "Lunch")) throw new AppException("Choose a work order, an indirect task, a break, or lunch.");
        var clock = await ClockForAsync(who.Id, input.DayClockId, start, ct);
        if (start < clock.StartedAt || (clock.EndedAt is { } stop && end > stop)) throw new AppException("The task must fall inside the clocked-in day.");
        if (await db.SequflohDaySegments.AnyAsync(s => s.DayClockId == clock.Id && s.StartedAt < end && (s.EndedAt ?? DateTimeOffset.MaxValue) > start, ct))
            throw new ConflictException("That time overlaps another task on the day.");
        var seg = Begin(clock, kind, start, null);
        seg.EndedAt = end;
        seg.EndKind = "Stop";
        seg.Seconds = (long)(end - start).TotalSeconds;
        seg.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        string label;
        if (kind == "WorkOrder")
        {
            var rec = await db.SequflohTrackingRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == input.TrackingRecordId, ct) ?? throw new NotFoundException("Work order");
            seg.TrackingRecordId = rec.Id;
            seg.TrackingNumber = rec.Number;
            seg.TaskName = rec.Number;
            var entry = new SequflohTimeEntry { TrackingRecordId = rec.Id, TrackingNumber = rec.Number, UserId = who.Id, UserName = who.Name, StartedAt = start, AddedBy = actor, AddedReason = reason };
            SequflohTimeService.RewriteInterval(entry, start, end);
            db.SequflohTimeEntries.Add(entry);
            seg.TimeEntryId = entry.Id;
            label = $"work order {rec.Number}";
        }
        else if (kind == "Indirect")
        {
            var task = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == input.IndirectValueId && v.ListKey == ListKey, ct) ?? throw new AppException("Choose an indirect task.");
            seg.IndirectValueId = task.Id;
            seg.TaskName = task.Label;
            label = task.Label;
        }
        else
        {
            seg.TaskName = kind;
            label = kind.ToLowerInvariant();
        }
        Audit("DayClockCorrected", clock.Id, $"{actor} added {label} for {who.Name} from {Stamp(start)} to {Stamp(end)}. Reason: {reason}", reason);
        await db.SaveChangesAsync(ct);
    }

    async Task AdjustSegmentAsync((Guid Id, string Name, Guid? DepartmentId) who, CorrectionInput input, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var seg = await db.SequflohDaySegments.FirstOrDefaultAsync(s => s.Id == input.SegmentId && s.UserId == who.Id, ct) ?? throw new NotFoundException("Task");
        var clock = await db.SequflohDayClocks.FirstAsync(c => c.Id == seg.DayClockId, ct);
        var start = input.StartedAt ?? seg.StartedAt;
        var end = input.EndedAt ?? seg.EndedAt ?? throw new AppException("An open task is corrected by clocking out or by setting an end time.");
        if (end <= start) throw new AppException("The task must end after it starts.");
        NotFuture(start, now); NotFuture(end, now);
        if (start < clock.StartedAt || (clock.EndedAt is { } stop && end > stop)) throw new AppException("The task must fall inside the clocked-in day.");
        if (await db.SequflohDaySegments.AnyAsync(s => s.DayClockId == clock.Id && s.Id != seg.Id && s.StartedAt < end && (s.EndedAt ?? DateTimeOffset.MaxValue) > start, ct))
            throw new ConflictException("That time overlaps another task on the day.");
        if (seg.OriginalStartedAt is null) { seg.OriginalStartedAt = seg.StartedAt; seg.OriginalEndedAt = seg.EndedAt; }
        var before = $"{Stamp(seg.StartedAt)} to {(seg.EndedAt is { } o ? Stamp(o) : "now")}";
        seg.StartedAt = start;
        seg.EndedAt = end;
        seg.Seconds = (long)(end - start).TotalSeconds;
        seg.AdjustedAt = now;
        seg.AdjustedBy = actor;
        seg.AdjustReason = reason;
        if (!string.IsNullOrWhiteSpace(input.Note)) seg.Note = input.Note.Trim();
        if (seg.TimeEntryId is { } entryId)
        {
            var entry = await db.SequflohTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
            if (entry is not null)
            {
                if (entry.OriginalStartedAt is null) { entry.OriginalStartedAt = entry.StartedAt; entry.OriginalStoppedAt = entry.StoppedAt; }
                SequflohTimeService.RewriteInterval(entry, start, end);
                entry.AdjustedAt = now;
                entry.AdjustedBy = actor;
                entry.AdjustReason = reason;
            }
        }
        Audit("DayClockCorrected", clock.Id, $"{actor} changed {who.Name}'s {seg.TaskName ?? seg.Kind} from {before} to {Stamp(start)} to {Stamp(end)}. Reason: {reason}", reason);
        await db.SaveChangesAsync(ct);
    }

    async Task VoidSegmentAsync((Guid Id, string Name, Guid? DepartmentId) who, CorrectionInput input, string reason, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var seg = await db.SequflohDaySegments.FirstOrDefaultAsync(s => s.Id == input.SegmentId && s.UserId == who.Id, ct) ?? throw new NotFoundException("Task");
        seg.VoidedAt = now;
        seg.VoidedBy = actor;
        seg.VoidReason = reason;
        if (seg.EndedAt is null) Close(seg, now, "Stop");
        if (seg.TimeEntryId is { } entryId)
        {
            var entry = await db.SequflohTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
            if (entry is not null)
            {
                if (entry.StoppedAt is null) SequflohTimeService.Finish(entry, now);
                entry.VoidedAt = now;
                entry.VoidedBy = actor;
                entry.VoidReason = reason;
            }
        }
        Audit("DayClockCorrected", seg.DayClockId, $"{actor} removed {who.Name}'s {seg.TaskName ?? seg.Kind} ({Stamp(seg.StartedAt)}). It stays in the history and no longer counts. Reason: {reason}", reason);
        await db.SaveChangesAsync(ct);
    }
}
