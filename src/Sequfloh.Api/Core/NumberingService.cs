using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Common;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Services;

public interface INumberingService
{
    /// <summary>Allocates the next number. Must be called inside an open transaction; the row lock is held until commit, so numbers are unique and gap-free per committed transaction.</summary>
    Task<string> NextAsync(string recordType, Guid? siteId, CancellationToken ct = default);

    /// <summary>
    /// Reserves a block of consecutive numbers for a bulk operation with one update of the sequence instead of one per number.
    /// Same transaction rule as <see cref="NextAsync"/>; numbers the operation did not use are handed back with <see cref="ReturnUnusedAsync"/>
    /// before the transaction commits, so the committed sequence stays gap-free.
    /// </summary>
    Task<NumberBlock> ReserveAsync(string recordType, Guid? siteId, int count, CancellationToken ct = default);

    /// <summary>Hands back the unused end of a block (when no later block was reserved from the same sequence).</summary>
    Task ReturnUnusedAsync(NumberBlock block, CancellationToken ct = default);
}

/// <summary>A reserved run of numbers [next, end) of one sequence.</summary>
public sealed class NumberBlock(Guid sequenceId, string prefix, string yearPart, int padding, long next, long end)
{
    public Guid SequenceId { get; } = sequenceId;
    public long Next { get; private set; } = next;
    public long End { get; } = end;
    public bool IsEmpty => Next >= End;

    public bool TryTake(out string number)
    {
        if (IsEmpty) { number = ""; return false; }
        number = $"{prefix}{yearPart}{(Next++).ToString().PadLeft(padding, '0')}";
        return true;
    }
}

public sealed class NumberingService(AppDbContext db, IClock clock) : INumberingService
{
    public async Task<string> NextAsync(string recordType, Guid? siteId, CancellationToken ct = default)
    {
        var block = await ReserveAsync(recordType, siteId, 1, ct);
        block.TryTake(out var number);
        return number;
    }

    public async Task<NumberBlock> ReserveAsync(string recordType, Guid? siteId, int count, CancellationToken ct = default)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var seq = await LockAsync(recordType, siteId, ct);
        var year = clock.UtcNow.Year;
        if (seq.ResetYearly && seq.Year != year) { seq.Year = year; seq.NextValue = 1; }
        var first = seq.NextValue;
        seq.NextValue += count;
        await db.SaveChangesAsync(ct);
        return new NumberBlock(seq.Id, seq.Prefix, seq.ResetYearly ? $"{year}-" : "", seq.Padding, first, first + count);
    }

    public async Task ReturnUnusedAsync(NumberBlock block, CancellationToken ct = default)
    {
        if (block.IsEmpty) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Numbers must be returned inside the transaction that reserved them.");
        var seq = await db.NumberSequences.FromSqlInterpolated($"SELECT * FROM sqf.number_sequences WHERE \"Id\" = {block.SequenceId} FOR UPDATE").FirstAsync(ct);
        if (seq.NextValue != block.End) return; // a later block was taken; the gap cannot be closed
        seq.NextValue = block.Next;
        await db.SaveChangesAsync(ct);
    }

    private async Task<NumberSequence> LockAsync(string recordType, Guid? siteId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Numbers must be allocated inside a transaction.");

        var seq = await db.NumberSequences
            .FromSqlInterpolated($"SELECT * FROM sqf.number_sequences WHERE \"RecordType\" = {recordType} AND (\"SiteId\" = {siteId} OR (\"SiteId\" IS NULL AND {siteId}::uuid IS NULL)) FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (seq is null && siteId is not null)
        {
            seq = await db.NumberSequences
                .FromSqlInterpolated($"SELECT * FROM sqf.number_sequences WHERE \"RecordType\" = {recordType} AND \"SiteId\" IS NULL FOR UPDATE")
                .FirstOrDefaultAsync(ct);
        }
        return seq ?? throw new AppException($"No numbering sequence is configured for {recordType}.");
    }
}
