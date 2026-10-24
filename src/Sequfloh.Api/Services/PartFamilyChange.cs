using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Which parts a product family change applies to. Mode "ids" uses the ticked PartIds; "filter" uses every part matching the Part Master search and filters;
/// "folder" uses the parts of SourceTypeId linked to any drawing in FolderId or its subfolders (or linked to one of those folders); "pattern" uses the parts of
/// SourceTypeId (or all parts) with a number matching Pattern. A Pattern given with another mode narrows that selection further.
/// </summary>
public sealed record FamilyChangeRequest(string Mode, Guid? TargetTypeId = null, List<Guid>? PartIds = null, string? Q = null, string? PartType = null, string? Status = null, string? Tag = null,
    Guid? FolderId = null, Guid? SourceTypeId = null, string? Pattern = null, string? PatternMode = null);

public sealed record FamilyChangeRow(Guid Id, string PartNumber, string Name, string? DrawingNumber, Dictionary<string, string> Numbers, string? Family, string? Drawing);
public sealed record FamilyChangePreview(int Count, int AlreadyInTarget, string? Target, string? Source, List<FamilyChangeRow> Parts, List<FamilyChangeRow>? Unmatched);

/// <summary>
/// Moves many parts to another product family in one go. Only the family changes: attribute values are kept as they are (values of fields the new family
/// does not use stay stored and come back when the part is moved back). Every part change is audited with the old and new family, plus one summary entry per run.
/// </summary>
public sealed class PartFamilyChangeService(AppDbContext db, ModuleSupport m, SequflohPartsService parts)
{
    async Task<IQueryable<Part>> EditableAsync(CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.MasterDataEdit, ct)) throw new ForbiddenException();
        return await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohEditParts, ct);
    }

    async Task<(Guid Id, string Label)?> FamilyAsync(Guid? id, bool active, CancellationToken ct)
    {
        if (id is null) return null;
        var v = await db.SequflohReferenceValues.AsNoTracking().Where(x => x.Id == id && x.ListKey == SequflohCatalog.ProductFamilies && (!active || x.IsActive))
            .Select(x => new { x.Id, x.Label }).FirstOrDefaultAsync(ct);
        return v is null ? throw new AppException(active ? "Choose an active product family to move the parts to." : "The product family was not found.") : (v.Id, v.Label);
    }

    /// <summary>The folder and every folder below it.</summary>
    async Task<List<Guid>> SubtreeAsync(Guid folderId, CancellationToken ct)
    {
        if (!await db.DrawingFolders.AnyAsync(f => f.Id == folderId, ct)) throw new NotFoundException("Drawing folder");
        var all = await db.DrawingFolders.AsNoTracking().Where(f => !f.IsArchived && f.ParentId != null).Select(f => new { f.Id, f.ParentId }).ToListAsync(ct);
        var children = all.ToLookup(f => f.ParentId!.Value, f => f.Id);
        var result = new List<Guid> { folderId };
        for (var i = 0; i < result.Count; i++) result.AddRange(children[result[i]]);
        return result;
    }

    /// <summary>True when one of the part's numbers (OR #, drawing number or any attribute value) matches the pattern, ignoring case.</summary>
    public static bool NumberMatches(Part p, string pattern, string? mode)
    {
        var t = pattern.Trim();
        bool Hit(string? v) => !string.IsNullOrWhiteSpace(v) && (mode switch
        {
            "contains" => v.Contains(t, StringComparison.OrdinalIgnoreCase),
            "ends" => v.Trim().EndsWith(t, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(v.Trim(), t, StringComparison.OrdinalIgnoreCase),
            _ => v.Trim().StartsWith(t, StringComparison.OrdinalIgnoreCase),
        });
        return Hit(p.PartNumber) || Hit(p.DrawingNumber) || PartAttributes.Read(p.Attributes).Values.Any(Hit);
    }

    async Task<List<Part>> SelectAsync(FamilyChangeRequest r, CancellationToken ct)
    {
        var q = await EditableAsync(ct);
        var pattern = string.IsNullOrWhiteSpace(r.Pattern) ? null : r.Pattern.Trim();
        switch (r.Mode)
        {
            case "ids":
                var ids = (r.PartIds ?? []).Distinct().ToList();
                if (ids.Count == 0) throw new AppException("Tick at least one part.");
                q = q.Where(p => ids.Contains(p.Id));
                break;
            case "filter":
                var filtered = await parts.PartsQueryAsync(r.Q, null, null, Enum.TryParse<MasterStatus>(r.Status, true, out var st) ? st : null, null, ct, r.PartType);
                if (r.Tag == "none") filtered = filtered.Where(x => x.StatusTag == null); else if (!string.IsNullOrWhiteSpace(r.Tag)) filtered = filtered.Where(x => x.StatusTag == r.Tag);
                var fids = filtered.Select(p => p.Id);
                q = q.Where(p => fids.Contains(p.Id));
                break;
            case "folder":
                if (r.FolderId is not { } folder) throw new AppException("Choose a drawing folder.");
                if (r.SourceTypeId is null) throw new AppException("Choose the product family the parts are in now.");
                var folders = await SubtreeAsync(folder, ct);
                var drawings = db.Drawings.Where(d => !d.IsArchived && folders.Contains(d.FolderId)).Select(d => d.Id);
                var linked = db.DrawingPartLinks.Where(l => drawings.Contains(l.DrawingId)).Select(l => l.PartId);
                var folderParts = db.DrawingFolders.Where(f => folders.Contains(f.Id) && f.PartId != null).Select(f => f.PartId!.Value);
                q = q.Where(p => p.TypeId == r.SourceTypeId && (linked.Contains(p.Id) || folderParts.Contains(p.Id)));
                break;
            case "pattern":
                if (pattern is null) throw new AppException("Enter the part number pattern, for example INJ.");
                if (r.SourceTypeId is { } src) q = q.Where(p => p.TypeId == src);
                break;
            default: throw new AppException("Choose how to select the parts.");
        }
        var list = await q.OrderBy(p => p.PartNumber).ToListAsync(ct);
        return pattern is null ? list : list.Where(p => NumberMatches(p, pattern, r.PatternMode)).ToList();
    }

    async Task<Dictionary<Guid, string>> DrawingNamesAsync(List<Guid> partIds, List<Guid>? folders, CancellationToken ct)
    {
        var names = new Dictionary<Guid, List<string>>();
        foreach (var chunk in partIds.Chunk(5000))
        {
            var rows = await (from l in db.DrawingPartLinks.AsNoTracking()
                              join d in db.Drawings.AsNoTracking() on l.DrawingId equals d.Id
                              where chunk.Contains(l.PartId) && !d.IsArchived && (folders == null || folders.Contains(d.FolderId))
                              select new { l.PartId, d.Title }).ToListAsync(ct);
            foreach (var x in rows) { if (!names.TryGetValue(x.PartId, out var n)) names[x.PartId] = n = []; n.Add(x.Title); }
        }
        return names.ToDictionary(x => x.Key, x => string.Join(", ", x.Value.Distinct().Order().Take(5)) + (x.Value.Distinct().Count() > 5 ? ", …" : ""));
    }

    async Task<List<FamilyChangeRow>> RowsAsync(List<Part> list, List<Guid>? folders, CancellationToken ct)
    {
        var drawings = await DrawingNamesAsync(list.Select(p => p.Id).ToList(), folders, ct);
        var typeIds = list.Where(p => p.TypeId != null).Select(p => p.TypeId!.Value).Distinct().ToList();
        var labels = await db.SequflohReferenceValues.AsNoTracking().Where(v => typeIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Label, ct);
        return list.Select(p => new FamilyChangeRow(p.Id, p.PartNumber, p.Name, p.DrawingNumber, PartAttributes.Read(p.Attributes),
            p.TypeId is { } t && labels.TryGetValue(t, out var l) ? l : p.ProductFamily, drawings.GetValueOrDefault(p.Id))).ToList();
    }

    /// <summary>Parts of the family with no drawing linked at all (by link or by a part folder): the ones to check by hand.</summary>
    async Task<List<Part>> UnmatchedAsync(Guid sourceTypeId, CancellationToken ct)
    {
        var q = await EditableAsync(ct);
        var linked = db.DrawingPartLinks.Join(db.Drawings.Where(d => !d.IsArchived), l => l.DrawingId, d => d.Id, (l, d) => l.PartId);
        var folderParts = db.DrawingFolders.Where(f => !f.IsArchived && f.PartId != null).Select(f => f.PartId!.Value);
        return await q.Where(p => p.TypeId == sourceTypeId && !linked.Contains(p.Id) && !folderParts.Contains(p.Id)).OrderBy(p => p.PartNumber).ToListAsync(ct);
    }

    public async Task<FamilyChangePreview> PreviewAsync(FamilyChangeRequest r, CancellationToken ct)
    {
        var target = await FamilyAsync(r.TargetTypeId, false, ct);
        var source = await FamilyAsync(r.SourceTypeId, false, ct);
        var list = await SelectAsync(r, ct);
        var folders = r.Mode == "folder" && r.FolderId is { } f ? await SubtreeAsync(f, ct) : null;
        var already = target is null ? 0 : list.Count(p => p.TypeId == target.Value.Id);
        var unmatched = source is null ? null : await RowsAsync(await UnmatchedAsync(source.Value.Id, ct), null, ct);
        return new FamilyChangePreview(list.Count - already, already, target?.Label, source?.Label, await RowsAsync(list, folders, ct), unmatched);
    }

    /// <summary>The source family's parts with no linked drawing, as an Excel workbook.</summary>
    public async Task<byte[]> UnmatchedXlsxAsync(Guid sourceTypeId, CancellationToken ct)
    {
        var source = (await FamilyAsync(sourceTypeId, false, ct))!.Value;
        var rows = await RowsAsync(await UnmatchedAsync(sourceTypeId, ct), null, ct);
        var keys = PartAttributes.All.Where(a => rows.Any(r => r.Numbers.ContainsKey(a.Key))).ToList();
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Unmatched parts");
        string[] head = ["Part number", "Name", "Drawing number", .. keys.Select(k => k.Label), "Product family"];
        for (var i = 0; i < head.Length; i++) { ws.Cell(1, i + 1).Value = head[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        var n = 2;
        foreach (var r in rows)
        {
            string?[] cells = [r.PartNumber, r.Name, r.DrawingNumber, .. keys.Select(k => r.Numbers.GetValueOrDefault(k.Key)), r.Family ?? source.Label];
            for (var i = 0; i < cells.Length; i++) { ws.Cell(n, i + 1).SetValue(cells[i] ?? ""); ws.Cell(n, i + 1).Style.NumberFormat.Format = "@"; }
            n++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(n, 500));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<object> ApplyAsync(FamilyChangeRequest r, CancellationToken ct)
    {
        var target = await FamilyAsync(r.TargetTypeId, true, ct) ?? throw new AppException("Choose the product family to move the parts to.");
        var list = (await SelectAsync(r, ct)).Where(p => p.TypeId != target.Id).ToList();
        if (list.Count == 0) throw new AppException("None of the selected parts need to change.");
        var from = list.GroupBy(p => p.ProductFamily ?? "no product family").Select(g => $"{g.Count()} from {g.Key}").ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var chunk in list.Select(p => p.Id).Chunk(500))
        {
            var tracked = await db.Parts.Where(p => chunk.Contains(p.Id)).ToListAsync(ct);
            // only the family changes; the attribute values stay stored as they are
            foreach (var p in tracked) { p.TypeId = target.Id; p.ProductFamily = target.Label; }
            await db.SaveChangesAsync(ct);
        }
        var how = r.Mode switch
        {
            "folder" => $"parts linked to drawings in folder {await db.DrawingFolders.Where(f => f.Id == r.FolderId).Select(f => f.Name).FirstOrDefaultAsync(ct)} and its subfolders",
            "pattern" => "parts matching a number pattern",
            "filter" => "all parts matching the Part Master filters",
            _ => "ticked parts",
        };
        if (!string.IsNullOrWhiteSpace(r.Pattern)) how += $", number {r.PatternMode switch { "contains" => "contains", "ends" => "ends with", "equals" => "is", _ => "starts with" }} \"{r.Pattern.Trim()}\"";
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = "Changed product family", EntityType = "PartFamilyChange", EntityId = target.Id,
            Description = $"Moved {list.Count} part{(list.Count == 1 ? "" : "s")} to product family {target.Label} ({string.Join(", ", from)}); selected by {how}.",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { Changed = list.Count, Target = target.Label };
    }
}
