using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Drawings;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Drawings;

/// <summary>
/// Links drawings to parts by name. A folder or file name (without its extension) links to every part whose OR #, SENSEN # or family drawing number field
/// (see PartAttributes.DrawingNumberFields, for example the JH PN of fuel pumps) is the same, ignoring case, spaces, dashes, underscores and a family prefix from the drawing prefixes list (so D1004Z.pdf links to INJ-D1004Z). One drawing can
/// link to many parts. Links are only ever added here, never removed, so links made by hand are kept.
/// </summary>
public sealed class DrawingPartMatcher(AppDbContext db, ICurrentUser me)
{
    public sealed record PartHit(Guid Id, string PartNumber);

    /// <summary>The form names are compared in: no extension for drawing files, upper case, no spaces, dashes, underscores or slashes.</summary>
    public static string Normalize(string? name)
    {
        var s = (name ?? "").Trim();
        var ext = Path.GetExtension(s);
        if (ext.Length > 1 && DrawingService.Accepted.Contains(ext.ToLowerInvariant())) s = s[..^ext.Length];
        var n = new string(s.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_' and not '/' and not '\\').Select(char.ToUpperInvariant).ToArray());
        // placeholders such as "/", "-", "N/A" or "none" are not numbers and never link anything
        return !n.Any(char.IsLetterOrDigit) || Placeholders.Contains(n) ? "" : n;
    }

    static readonly HashSet<string> Placeholders = ["NA", "NONE", "TBD", "TBA", "NULL", "UNKNOWN", "0"];

    /// <summary>Every part by each of its matching numbers (normalized).</summary>
    public async Task<Dictionary<string, List<PartHit>>> IndexAsync(CancellationToken ct, IReadOnlyCollection<Guid>? only = null)
    {
        var fields = PartAttributes.DrawingNumberFields.Select(a => a.Key).ToList();
        var q = db.Parts.AsNoTracking();
        if (only is not null) q = q.Where(p => only.Contains(p.Id));
        var rows = await q.Select(p => new { p.Id, p.PartNumber, p.DrawingNumber, p.Attributes }).ToListAsync(ct);
        var prefixes = await PrefixesAsync(ct);
        var map = new Dictionary<string, List<PartHit>>();
        foreach (var r in rows)
        {
            var attrs = PartAttributes.Read(r.Attributes);
            var names = new[] { r.PartNumber, r.DrawingNumber }.Concat(fields.Select(f => attrs.GetValueOrDefault(f)));
            foreach (var n in names.Select(Normalize).Where(n => n.Length > 0).SelectMany(n => WithPrefixes(n, prefixes)).Distinct())
            {
                if (!map.TryGetValue(n, out var l)) map[n] = l = [];
                l.Add(new PartHit(r.Id, r.PartNumber));
            }
        }
        return map;
    }

    /// <summary>The family prefixes (for example INJ-) a drawing or part number may leave out, from the admin-editable drawing prefixes list.</summary>
    public async Task<List<string>> PrefixesAsync(CancellationToken ct) =>
        (await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.DrawingPrefixes && v.IsActive).Select(v => v.Label).ToListAsync(ct))
            .Select(Normalize).Where(p => p.Length > 0).Distinct().ToList();

    /// <summary>A normalized number plus the same number with a family prefix taken off or put on, so D1004Z and INJ-D1004Z find each other.</summary>
    public static IEnumerable<string> WithPrefixes(string n, IReadOnlyCollection<string> prefixes)
    {
        yield return n;
        foreach (var p in prefixes)
        {
            if (n.StartsWith(p, StringComparison.Ordinal)) { if (n.Length > p.Length) yield return n[p.Length..]; }
            else yield return p + n;
        }
    }

    public static List<PartHit> Match(Dictionary<string, List<PartHit>> index, IEnumerable<string?> names) =>
        names.Select(Normalize).Where(n => n.Length > 0).Distinct().SelectMany(n => index.GetValueOrDefault(n) ?? []).DistinctBy(x => x.Id).ToList();

    sealed record Candidate(Guid Id, Guid SiteId, Guid? DepartmentId, string Title, string? DrawingNumber, string? FolderName);

    IQueryable<Candidate> Candidates() => db.Drawings.AsNoTracking().Where(d => !d.IsArchived)
        .Select(d => new Candidate(d.Id, d.SiteId, d.DepartmentId, d.Title, d.DrawingNumber, db.DrawingFolders.Where(f => f.Id == d.FolderId).Select(f => f.Name).FirstOrDefault()));

    /// <summary>Adds the missing links between the drawings and the parts (all parts when partIds is null). Returns drawings checked and links added.</summary>
    public async Task<(int Checked, int Added, List<(Guid DrawingId, Guid SiteId, string Title, int Added)> PerDrawing)> LinkAsync(IReadOnlyCollection<Guid>? drawingIds, IReadOnlyCollection<Guid>? partIds, CancellationToken ct, Func<Guid, Guid?, bool>? allow = null)
    {
        var index = await IndexAsync(ct, partIds);
        var per = new List<(Guid, Guid, string, int)>();
        if (index.Count == 0) return (0, 0, per);
        var q = Candidates();
        if (drawingIds is not null) q = q.Where(d => drawingIds.Contains(d.Id));
        var drawings = (await q.ToListAsync(ct)).Where(d => allow is null || allow(d.SiteId, d.DepartmentId)).ToList();
        var have = new HashSet<(Guid, Guid)>();
        var ids = drawings.Select(d => d.Id).ToList();
        foreach (var chunk in ids.Chunk(5000))
            foreach (var l in await db.DrawingPartLinks.AsNoTracking().Where(l => chunk.Contains(l.DrawingId)).Select(l => new { l.DrawingId, l.PartId }).ToListAsync(ct))
                have.Add((l.DrawingId, l.PartId));
        var added = 0;
        foreach (var d in drawings)
        {
            var n = 0;
            foreach (var p in Match(index, [d.Title, d.DrawingNumber, d.FolderName]))
                if (have.Add((d.Id, p.Id))) { db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = d.Id, PartId = p.Id, CreatedAt = DateTimeOffset.UtcNow }); n++; }
            if (n > 0) { per.Add((d.Id, d.SiteId, d.Title, n)); added += n; }
        }
        return (drawings.Count, added, per);
    }

    /// <summary>After parts are added or changed: links them to drawings already named with one of their numbers, with one audit entry per drawing.</summary>
    public async Task<int> LinkPartsAsync(IReadOnlyCollection<Guid> partIds, CancellationToken ct)
    {
        if (partIds.Count == 0) return 0;
        var (_, added, per) = await LinkAsync(null, partIds, ct);
        foreach (var (id, site, title, n) in per)
            db.AuditEntries.Add(new AuditEntry
            {
                OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Linked drawing to parts", EntityType = nameof(Drawing), EntityId = id, SiteId = site,
                CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Drawing {title} linked to {n} part{(n == 1 ? "" : "s")} with a matching drawing number after the parts were saved"),
            });
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}
