using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Tracking;

/// <summary>
/// Job time on work orders for chargebacks. Every signed-in person can clock on and off any work order, whether or not they are assigned, and several
/// people can be clocked on the same work order at once. A pause records why (break or lunch) and is not counted in man hours.
/// This is time on the job only; it is not a payroll clock.
/// </summary>
public sealed class SequflohTimeService(AppDbContext db, ICurrentUser me)
{
    Guid UserId => me.UserId ?? throw new ForbiddenException();

    Task<DateTimeOffset> NowAsync(CancellationToken ct) => db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    async Task<SequflohTrackingRecord> RecordAsync(Guid id, CancellationToken ct) =>
        await db.SequflohTrackingRecords.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order");

    public async Task StartAsync(Guid id, CancellationToken ct)
    {
        var rec = await RecordAsync(id, ct);
        if (await db.SequflohTimeEntries.AnyAsync(e => e.TrackingRecordId == id && e.UserId == UserId && e.StoppedAt == null, ct))
            throw new ConflictException("You are already clocked on this job.");
        var name = await db.Users.Where(u => u.Id == UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? me.UserName ?? "";
        var at = await NowAsync(ct);
        var entry = new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = rec.Number, UserId = UserId, UserName = name, StartedAt = at };
        AddEvent(entry, "Start", at);
        db.SequflohTimeEntries.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("You are already clocked on this job."); }
    }

    public sealed record TimeEvent(string Kind, DateTimeOffset At, string? Reason = null);
    public static readonly string[] PauseReasons = ["Break", "Lunch"];

    /// <summary>How an event reads in the time log.</summary>
    public static string Label(TimeEvent v) => v.Kind switch
    {
        "Start" => "Clocked on job",
        "Pause" => v.Reason is { } r ? $"Paused for {r.ToLowerInvariant()}" : "Paused job",
        "Resume" => "Clocked back on job",
        "Stop" => "Clocked off job",
        _ => v.Kind,
    };
    static string? LastPauseReason(SequflohTimeEntry e) => e.PausedAt is null ? null : Stored(e).LastOrDefault(v => v.Kind == "Pause")?.Reason;
    public static List<TimeEvent> EventsOf(SequflohTimeEntry e)
    {
        var list = JsonSerializer.Deserialize<List<TimeEvent>>(e.Events, SequflohJson.Options) ?? [];
        if (list.Count > 0) return list;
        // entries from before the event log: show the start and stop
        return e.StoppedAt is { } s ? [new("Start", e.StartedAt), new("Stop", s)] : [new("Start", e.StartedAt)];
    }
    static List<TimeEvent> Stored(SequflohTimeEntry e) => JsonSerializer.Deserialize<List<TimeEvent>>(e.Events, SequflohJson.Options) ?? [];
    static void AddEvent(SequflohTimeEntry e, string kind, DateTimeOffset at, string? reason = null)
    {
        var list = Stored(e);
        if (list.Count == 0 && kind != "Start") list.Add(new("Start", e.StartedAt));
        list.Add(new(kind, at, reason));
        e.Events = JsonSerializer.Serialize(list, SequflohJson.Options);
    }

    /// <summary>Worked seconds for an entry at a moment: paused time is never counted.</summary>
    public static long Worked(SequflohTimeEntry e, DateTimeOffset now)
    {
        if (e.StoppedAt is not null) return e.Seconds;
        var paused = e.PausedSeconds + (e.PausedAt is { } p ? Math.Max(0, (long)(now - p).TotalSeconds) : 0);
        return Math.Max(0, (long)(now - e.StartedAt).TotalSeconds - paused);
    }
    public static long Paused(SequflohTimeEntry e, DateTimeOffset now) => e.PausedSeconds + (e.StoppedAt is null && e.PausedAt is { } p ? Math.Max(0, (long)(now - p).TotalSeconds) : 0);

    Task<SequflohTimeEntry> RunningAsync(Guid id, CancellationToken ct) => db.SequflohTimeEntries.FirstOrDefaultAsync(x => x.TrackingRecordId == id && x.UserId == UserId && x.StoppedAt == null, ct)!;

    public async Task PauseAsync(Guid id, string? reason, CancellationToken ct)
    {
        var why = PauseReasons.FirstOrDefault(r => string.Equals(r, reason?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException("Choose why you are pausing: Break or Lunch.");
        var e = await RunningAsync(id, ct) ?? throw new ConflictException("You are not clocked on this job.");
        if (e.PausedAt is not null) throw new ConflictException("This job is already paused.");
        var now = await NowAsync(ct);
        e.PausedAt = now; AddEvent(e, "Pause", now, why);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResumeAsync(Guid id, CancellationToken ct)
    {
        var e = await RunningAsync(id, ct) ?? throw new ConflictException("You are not clocked on this job.");
        if (e.PausedAt is not { } p) throw new ConflictException("This job is not paused.");
        var now = await NowAsync(ct);
        e.PausedSeconds += Math.Max(0, (long)(now - p).TotalSeconds); e.PausedAt = null; AddEvent(e, "Resume", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task StopAsync(Guid id, CancellationToken ct)
    {
        var e = await RunningAsync(id, ct) ?? throw new ConflictException("You are not clocked on this job.");
        var now = await NowAsync(ct);
        if (e.PausedAt is { } p) { e.PausedSeconds += Math.Max(0, (long)(now - p).TotalSeconds); e.PausedAt = null; AddEvent(e, "Resume", now); }
        e.Seconds = Worked(e, now);
        e.StoppedAt = now; AddEvent(e, "Stop", now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Total time on a work order and the time per person, with who is running a timer right now.</summary>
    public async Task<object> SummaryAsync(Guid id, CancellationToken ct)
    {
        await RecordAsync(id, ct);
        var now = await NowAsync(ct);
        var entries = await db.SequflohTimeEntries.AsNoTracking().Where(e => e.TrackingRecordId == id).OrderBy(e => e.StartedAt).ToListAsync(ct);
        long Sec(SequflohTimeEntry e) => Worked(e, now);
        var people = entries.GroupBy(e => e.UserId).Select(g => new
        {
            UserId = g.Key, Name = g.Last().UserName, Seconds = g.Sum(Sec), PausedSeconds = g.Sum(e => Paused(e, now)), Entries = g.Count(),
            RunningSince = g.FirstOrDefault(e => e.StoppedAt is null && e.PausedAt is null)?.StartedAt,
            PausedSince = g.FirstOrDefault(e => e.StoppedAt is null)?.PausedAt,
            PauseReason = g.Where(e => e.StoppedAt is null).Select(LastPauseReason).FirstOrDefault(),
        }).OrderByDescending(p => p.Seconds).ToList();
        return new
        {
            TotalSeconds = entries.Sum(Sec), People = people, ServerTime = now,
            MineRunning = entries.Any(e => e.UserId == UserId && e.StoppedAt is null),
            MinePaused = entries.Any(e => e.UserId == UserId && e.StoppedAt is null && e.PausedAt is not null),
            MinePauseReason = entries.Where(e => e.UserId == UserId && e.StoppedAt is null).Select(LastPauseReason).FirstOrDefault(),
            PausedSeconds = entries.Sum(e => Paused(e, now)),
            Log = entries.SelectMany(e => EventsOf(e).Select(v => new { e.UserId, e.UserName, v.Kind, v.Reason, Label = Label(v), v.At })).OrderBy(v => v.At).ToList(),
            Entries = entries.OrderByDescending(e => e.StartedAt).Take(100).Select(e => new { e.Id, e.UserName, e.StartedAt, e.StoppedAt, Seconds = Sec(e), PausedSeconds = Paused(e, now), Paused = e.StoppedAt is null && e.PausedAt is not null }),
        };
    }
}
