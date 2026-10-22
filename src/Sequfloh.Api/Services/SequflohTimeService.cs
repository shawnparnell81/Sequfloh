using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tracking;

/// <summary>
/// Live time on work orders for chargebacks. Every signed-in person can start and stop their own timer on any work order, whether or not they are
/// assigned, and several people can run timers on the same work order at once. Starting and stopping are written to the audit trail.
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
            throw new ConflictException("Your timer is already running on this work order.");
        var name = await db.Users.Where(u => u.Id == UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? me.UserName ?? "";
        db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = id, TrackingNumber = rec.Number, UserId = UserId, UserName = name, StartedAt = await NowAsync(ct) });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("Your timer is already running on this work order."); }
    }

    public async Task StopAsync(Guid id, CancellationToken ct)
    {
        var e = await db.SequflohTimeEntries.FirstOrDefaultAsync(x => x.TrackingRecordId == id && x.UserId == UserId && x.StoppedAt == null, ct)
            ?? throw new ConflictException("Your timer is not running on this work order.");
        var now = await NowAsync(ct);
        e.StoppedAt = now;
        e.Seconds = Math.Max(0, (long)(now - e.StartedAt).TotalSeconds);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Total time on a work order and the time per person, with who is running a timer right now.</summary>
    public async Task<object> SummaryAsync(Guid id, CancellationToken ct)
    {
        await RecordAsync(id, ct);
        var now = await NowAsync(ct);
        var entries = await db.SequflohTimeEntries.AsNoTracking().Where(e => e.TrackingRecordId == id).OrderBy(e => e.StartedAt).ToListAsync(ct);
        long Sec(SequflohTimeEntry e) => e.StoppedAt is null ? Math.Max(0, (long)(now - e.StartedAt).TotalSeconds) : e.Seconds;
        var people = entries.GroupBy(e => e.UserId).Select(g => new
        {
            UserId = g.Key, Name = g.Last().UserName, Seconds = g.Sum(Sec), Entries = g.Count(),
            RunningSince = g.FirstOrDefault(e => e.StoppedAt is null)?.StartedAt,
        }).OrderByDescending(p => p.Seconds).ToList();
        return new
        {
            TotalSeconds = entries.Sum(Sec), People = people, ServerTime = now,
            MineRunning = entries.Any(e => e.UserId == UserId && e.StoppedAt is null),
            Entries = entries.OrderByDescending(e => e.StartedAt).Take(100).Select(e => new { e.Id, e.UserName, e.StartedAt, e.StoppedAt, Seconds = Sec(e) }),
        };
    }
}
