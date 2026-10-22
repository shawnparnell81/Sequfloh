using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.MasterData;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record RevisionRow(string Type, Guid RecordId, string Number, string Title, string Revision, string? Reason, DateTimeOffset At, string Link);

/// <summary>Cross-record SEQUFLOH queries: records related to a part, combined revision history.</summary>
public static class SequflohRelated
{
    public static async Task<object> ForPartAsync(AppDbContext db, Guid partId, CancellationToken ct)
    {
        var lists = await db.SequflohPartsLists.AsNoTracking().CountAsync(l => l.ParentPartId == partId || l.Items.Any(i => i.PartId == partId), ct);
        return new { PartsLists = lists };
    }

    public static async Task<object> RevisionHistoryAsync(AppDbContext db, IQueryable<Part> scopedParts, string? q, string? type, int page, int pageSize, CancellationToken ct)
    {
        var rows = new List<RevisionRow>();
        if (type is null or "" or "part")
            rows.AddRange(await db.PartRevisions.AsNoTracking().Join(scopedParts, r => r.PartId, p => p.Id, (r, p) => new RevisionRow("Part", p.Id, p.PartNumber, p.Name, r.Revision, r.Reason, r.CreatedAt, "/sequfloh/parts/" + p.Id)).ToListAsync(ct));
        if (type is null or "" or "parts-list")
            rows.AddRange(await db.SequflohPartsListRevisions.AsNoTracking().Join(db.SequflohPartsLists, r => r.PartsListId, l => l.Id, (r, l) => new RevisionRow("Parts list", l.Id, l.Number, l.Title, r.Revision, r.Reason, r.CreatedAt, "/sequfloh/parts-lists/" + l.Id)).ToListAsync(ct));
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(r => (r.Number + " " + r.Title + " " + r.Reason).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        pageSize = Math.Clamp(pageSize, 1, 200); page = Math.Max(1, page);
        return new { Items = rows.OrderByDescending(r => r.At).Skip((page - 1) * pageSize).Take(pageSize), Total = rows.Count };
    }
}
