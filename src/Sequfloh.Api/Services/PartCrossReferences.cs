using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.MasterData;
using Sequfloh.Drawings;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>Another customer's part that uses the same drawing. A view of existing parts, not a new record.</summary>
public sealed record SoldAs(Guid Id, string PartNumber, Guid? CustomerId, string? CustomerName, string? Upc, string? DrawingNumber);

/// <summary>
/// Parts that share a drawing number. The comparison uses the same normalization as drawing links, including a family's drawing-number attribute.
/// Blanks and placeholders match nothing. The current part is left out. A shared drawing, including a left and right pair, is listed with no warning.
/// </summary>
public static class PartCrossReferences
{
    public static List<string> Keys(string? drawing, string? attributes)
    {
        var names = new List<string?> { drawing };
        var read = PartAttributes.Read(attributes);
        names.AddRange(PartAttributes.DrawingNumberFields.Select(f => read.GetValueOrDefault(f.Key)));
        return names.Select(DrawingPartMatcher.Normalize).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Other parts in <paramref name="scoped"/> whose drawing matches one of these parts. One indexed lookup for the page, not the whole catalog.</summary>
    public static async Task<Dictionary<Guid, List<SoldAs>>> ForAsync(IReadOnlyList<(Guid Id, string? Drawing, string? Attributes)> parts, IQueryable<Part> scoped, IReadOnlyDictionary<Guid, string> customerNames, CancellationToken ct)
    {
        var own = parts.Select(p => (p.Id, Keys: Keys(p.Drawing, p.Attributes))).Where(p => p.Keys.Count > 0).ToList();
        var keys = own.SelectMany(p => p.Keys).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return [];
        var hits = await scoped.Where(p => p.DrawingKey != null && keys.Contains(p.DrawingKey))
            .Select(p => new Hit(p.Id, p.PartNumber, p.CustomerId, p.Upc, p.DrawingNumber, p.DrawingKey, p.Attributes)).ToListAsync(ct);
        var attrKeys = keys.Where(k => hits.All(h => h.DrawingKey != k)).ToList();
        if (attrKeys.Count > 0)
        {
            var seen = hits.Select(h => h.Id).ToHashSet();
            foreach (var key in attrKeys)
            {
                var raw = key;
                var more = await scoped.Where(p => p.AttributeText != null && EF.Functions.ILike(p.AttributeText, "%" + raw + "%"))
                    .Select(p => new Hit(p.Id, p.PartNumber, p.CustomerId, p.Upc, p.DrawingNumber, p.DrawingKey, p.Attributes)).ToListAsync(ct);
                hits.AddRange(more.Where(h => seen.Add(h.Id)));
            }
        }
        var byKey = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        foreach (var h in hits)
            foreach (var k in Keys(h.DrawingNumber, h.Attributes))
            {
                if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = [];
                list.Add(h);
            }
        var result = new Dictionary<Guid, List<SoldAs>>();
        foreach (var p in own)
        {
            var rows = p.Keys.SelectMany(k => byKey.GetValueOrDefault(k) ?? []).Where(h => h.Id != p.Id).DistinctBy(h => h.Id)
                .OrderBy(h => customerNames.GetValueOrDefault(h.CustomerId ?? Guid.Empty) ?? "\uffff", StringComparer.OrdinalIgnoreCase).ThenBy(h => h.PartNumber, StringComparer.OrdinalIgnoreCase)
                .Select(h => new SoldAs(h.Id, h.PartNumber, h.CustomerId, h.CustomerId is { } id ? customerNames.GetValueOrDefault(id) : null, h.Upc, h.DrawingNumber)).ToList();
            if (rows.Count > 0) result[p.Id] = rows;
        }
        return result;
    }

    sealed record Hit(Guid Id, string PartNumber, Guid? CustomerId, string? Upc, string? DrawingNumber, string? DrawingKey, string? Attributes);
}
