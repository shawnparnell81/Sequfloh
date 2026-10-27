using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.MasterData;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record PartExecutionInput(string? ProductFamily, string? AssemblyType, bool IsRepairable, bool IsSerialized, bool IsLotControlled, string? DrawingNumber, string? Specification, uint RowVersion = 0, bool IsRepairPart = false,
    string? PartClass = null, bool? IsAssembly = null, string? LotControl = null);
/// <summary>A part with its execution attributes, part type (from the part types list) and attribute values (see PartAttributes).</summary>
public sealed record SqfPartInput(SavePartRequest Part, PartExecutionInput Execution, Guid? TypeId = null, Dictionary<string, string?>? Attributes = null, string? Notes = null, List<string>? HiddenFields = null);
public sealed record PartsListInput(string Title, Guid? ParentPartId, string? Description, PartsListType ListType, uint RowVersion = 0, string? Number = null);
public sealed record PartsListItemInput(Guid? PartId, string? PartNumber, string? Description, decimal Quantity, string? Unit, bool Required, string? AlternatePartNumber,
    TraceabilityRequirement Traceability, string? Notes);
public sealed record ReviseInput(string? Reason);
public sealed record CopyItemsInput(Guid SourceListId);
public sealed record OperationStepInput(string Title, string? Category, string DetailedAction, string? DefaultTools, string? DefaultParts, string? Specification, decimal? Target,
    decimal? Minimum, decimal? Maximum, string? Unit, string? SafetyWarning, string? QualityCheckpoint, string? ExpectedResult, string? EvidenceRequirement, string? RequiredRole,
    string? RequiredQualification, bool IsActive = true, uint RowVersion = 0, string? Number = null);

/// <summary>
/// SEQUFLOH parts: execution attributes on the shared Part Master (no competing part store), controlled parts lists with revision history,
/// and reusable operation-step templates. Library records (parts lists, operation steps) are organization-wide; every write re-checks the permission.
/// </summary>
public sealed class SequflohPartsService(AppDbContext db, ModuleSupport m, IAccessService access, MasterDataService master, SequflohFoundationService f, Sequfloh.Drawings.DrawingPartMatcher drawingLinks)
{
    public const string PartsListType_ = "SqfPartsList";
    static string? T(string? s, int max) { if (string.IsNullOrWhiteSpace(s)) return null; s = s.Trim(); return s.Length > max ? throw new AppException($"A value is limited to {max} characters.") : s; }
    async Task RequireAsync(string permission, CancellationToken ct) { if (!await access.HasPermissionAsync(m.UserId, permission, ct)) throw new ForbiddenException(); }

    // ------------------------------------------------------------------ Part Master
    public async Task<Part> CreatePartAsync(SqfPartInput r, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohEditParts, r.Part.SiteId, r.Part.DepartmentId, ct);
        // the New part form requires a part type; other callers (import, older clients) may leave it empty and set it later
        await CheckTypeAsync(r.TypeId, null, ct);
        await CheckChoicesAsync(r.Execution, null, ct);
        if (r.Attributes is not null) PartAttributes.Clean(r.Attributes); // checked before anything is saved
        var p = await master.SavePartAsync(null, r.Part, ct); // shared Part Master rules: masterdata.edit, unique number, initial revision
        await ApplyExecutionAsync(p.Id, r.Execution with { RowVersion = 0 }, ct);
        await ApplyTypeAndAttributesAsync(p.Id, r, ct, isNew: true);
        await drawingLinks.LinkPartsAsync([p.Id], ct);
        return p;
    }

    public async Task UpdatePartAsync(Guid id, SqfPartInput r, CancellationToken ct)
    {
        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await m.RequireAsync(Permissions.SequflohEditParts, part.SiteId, part.DepartmentId, ct);
        if (r.TypeId is null && part.TypeId is not null) throw new AppException("Choose the part type.");
        await CheckTypeAsync(r.TypeId, part.TypeId, ct);
        await CheckChoicesAsync(r.Execution, part.PartClass, ct);
        if (r.Attributes is not null) PartAttributes.Clean(r.Attributes);
        // the OR # can be changed; it may match other parts only when this part's Position differs from theirs
        var number = r.Part.PartNumber?.Trim() ?? "";
        if (number != part.PartNumber)
        {
            var pos = (r.Attributes is not null ? r.Attributes.GetValueOrDefault("position") : PartAttributes.Read(part.Attributes).GetValueOrDefault("position"))?.Trim() ?? "";
            var others = await db.Parts.AsNoTracking().Where(x => x.PartNumber == number && x.Id != id).Select(x => x.Attributes).ToListAsync(ct);
            if (others.Any(a => string.Equals(PartAttributes.Read(a).GetValueOrDefault("position")?.Trim() ?? "", pos, StringComparison.OrdinalIgnoreCase)))
                throw new ConflictException(pos.Length > 0 ? $"A part with this part number and Position {pos} already exists." : "A part with this part number already exists. Give the part a Position that differs from the others.");
        }
        await master.SavePartAsync(id, r.Part, ct, numberChecked: true);
        await ApplyExecutionAsync(id, r.Execution with { RowVersion = 0 }, ct);
        await ApplyTypeAndAttributesAsync(id, r, ct);
        await drawingLinks.LinkPartsAsync([id], ct);
    }

    /// <summary>A new or changed product family must be an active value of the product families list; a part may keep a family that was deactivated later.</summary>
    async Task CheckTypeAsync(Guid? typeId, Guid? current, CancellationToken ct)
    {
        if (typeId is null || typeId == current) return;
        if (!await db.SequflohReferenceValues.AnyAsync(x => x.Id == typeId && x.ListKey == SequflohCatalog.ProductFamilies && x.IsActive, ct))
            throw new AppException("The product family is not an active value of the product families list.");
    }

    /// <summary>A new or changed part class must be an active value of the product families list; lot control must be one of its choices. Checked before anything is saved.</summary>
    async Task CheckChoicesAsync(PartExecutionInput r, string? current, CancellationToken ct)
    {
        var pc = T(r.PartClass, 100);
        if (pc is not null && pc != current && !await db.SequflohReferenceValues.AnyAsync(x => x.ListKey == SequflohCatalog.ProductFamilies && x.IsActive && x.Label == pc, ct))
            throw new AppException("The part class is not an active value of the product families list.");
        if (r.LotControl is not null && PartChoices.Lot(r.LotControl) is null) throw new AppException("Lot controlled must be Yes, No or Not applicable.");
    }

    async Task ApplyTypeAndAttributesAsync(Guid id, SqfPartInput r, CancellationToken ct, bool isNew = false)
    {
        var p = await db.Parts.FirstAsync(x => x.Id == id, ct);
        p.TypeId = r.TypeId;
        // the label is copied to ProductFamily so work instructions and filters that read the text keep working
        if (r.TypeId is { } t) p.ProductFamily = await db.SequflohReferenceValues.Where(x => x.Id == t).Select(x => x.Label).FirstAsync(ct);
        if (r.Attributes is not null)
        {
            // values of fields the part's family does not show are not on the form; they are kept, so nothing is lost when a part changes family and back
            var code = p.TypeId is { } tid ? await db.SequflohReferenceValues.Where(x => x.Id == tid).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
            var kept = PartAttributes.Read(p.Attributes).Where(kv => !r.Attributes.ContainsKey(kv.Key) && PartAttributes.Find(kv.Key) is { } d && !PartAttributes.AppliesTo(d, code));
            PartAttributes.Write(p, new Dictionary<string, string?>(r.Attributes.Concat(kept.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)))));
        }
        if (r.HiddenFields is not null) p.HiddenFields = PartFields.Write(r.HiddenFields);
        else if (isNew && r.TypeId is { } ft) // a new part starts with its family's Not applicable fields
            p.HiddenFields = await db.SequflohReferenceValues.Where(x => x.Id == ft).Select(x => x.HiddenFields).FirstOrDefaultAsync(ct);
        if (r.Notes is not null) p.Notes = r.Notes.Trim() is { Length: > 0 } n ? (n.Length <= 4000 ? n : throw new AppException("Notes can be at most 4000 characters.")) : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves only the SEQUFLOH execution attributes (repairable, serialized, lot-controlled, family, assembly type, drawing, specification).</summary>
    public async Task ApplyExecutionAsync(Guid id, PartExecutionInput r, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await m.RequireAsync(Permissions.SequflohEditParts, p.SiteId, p.DepartmentId, ct);
        if (r.RowVersion != 0) db.Entry(p).Property(x => x.RowVersion).OriginalValue = r.RowVersion;
        p.ProductFamily = T(r.ProductFamily, 100); p.AssemblyType = T(r.AssemblyType, 100); p.IsRepairable = r.IsRepairable; p.IsRepairPart = r.IsRepairPart; p.IsSerialized = r.IsSerialized;
        p.IsLotControlled = r.IsLotControlled; p.DrawingNumber = T(r.DrawingNumber, 100); p.Specification = T(r.Specification, 200);
        // older clients send only the lot flag; the form sends Yes, No or Not applicable
        if (r.LotControl is not null)
        {
            p.LotControl = PartChoices.Lot(r.LotControl) ?? throw new AppException("Lot controlled must be Yes, No or Not applicable.");
            p.IsLotControlled = p.LotControl == "Yes";
        }
        else p.LotControl = r.IsLotControlled ? "Yes" : p.LotControl == "Not applicable" ? "Not applicable" : "No";
        if (r.IsAssembly is { } asm) p.IsAssembly = asm;
        p.PartClass = T(r.PartClass, 100);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPartActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await m.RequireAsync(Permissions.SequflohEditParts, p.SiteId, p.DepartmentId, ct);
        await m.RequireAsync(Permissions.MasterDataEdit, p.SiteId, p.DepartmentId, ct);
        p.Status = active ? MasterStatus.Active : MasterStatus.Inactive; // parts are deactivated, never deleted
        await db.SaveChangesAsync(ct);
    }

    public async Task<IQueryable<Part>> PartsQueryAsync(string? q, string? view, string? family, MasterStatus? status, PartType? type, CancellationToken ct, string? partType = null)
    {
        var query = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct);
        if (view == "active") query = query.Where(x => x.Status == MasterStatus.Active);
        if (view == "repairable") query = query.Where(x => x.IsRepairable);
        if (view == "repair-parts") query = query.Where(x => x.IsRepairPart);
        if (view == "assemblies") query = query.Where(x => x.IsAssembly);
        if (!string.IsNullOrWhiteSpace(family)) query = query.Where(x => x.ProductFamily == family);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (type is not null) query = query.Where(x => x.PartType == type);
        if (partType == "none") query = query.Where(x => x.TypeId == null);
        else if (Guid.TryParse(partType, out var tid)) query = query.Where(x => x.TypeId == tid);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.PartNumber + " " + x.Name + " " + (x.Description ?? "") + " " + (x.DrawingNumber ?? "") + " " + (x.ProductFamily ?? "") + " " + (x.AttributeText ?? ""), like)); }
        return query;
    }

    public static IQueryable<Part> SortParts(IQueryable<Part> q, string? sort) => (sort ?? "partNumber:asc") switch
    {
        "partNumber:desc" => q.OrderByDescending(x => x.PartNumber),
        "name:asc" => q.OrderBy(x => x.Name), "name:desc" => q.OrderByDescending(x => x.Name),
        "productFamily:asc" => q.OrderBy(x => x.ProductFamily), "productFamily:desc" => q.OrderByDescending(x => x.ProductFamily),
        "updatedAt:asc" => q.OrderBy(x => x.UpdatedAt), "updatedAt:desc" => q.OrderByDescending(x => x.UpdatedAt),
        _ => q.OrderBy(x => x.PartNumber)
    };

    // ------------------------------------------------------------------ parts lists
    async Task<SequflohPartsList> LoadListAsync(Guid id, bool items, CancellationToken ct)
    {
        var q = db.SequflohPartsLists.AsQueryable();
        if (items) q = q.Include(x => x.Items);
        return await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Parts list");
    }

    static void Editable(SequflohPartsList l)
    {
        if (l.Status == PartsListStatus.Released) throw new ConflictException("The parts list is released; create a revision to change it.");
        if (l.Status == PartsListStatus.Inactive) throw new ConflictException("The parts list is inactive; reactivate it first.");
    }

    async Task ValidateParentAsync(Guid? partId, CancellationToken ct)
    {
        if (partId is { } p && !await db.Parts.AnyAsync(x => x.Id == p, ct)) throw new AppException("The parent part does not exist in the Part Master.");
    }

    public async Task<SequflohPartsList> CreateListAsync(PartsListInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        ModuleSupport.Required(r.Title, "Title", 300);
        await ValidateParentAsync(r.ParentPartId, ct);
        var number = RecordNumbers.Clean(r.Number, 40, "parts list number");
        await RecordNumbers.EnsureFreeAsync(db.SequflohPartsLists.Select(o => o.Number), number, "A parts list numbered", ct);
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var l = new SequflohPartsList { Number = number, Title = r.Title.Trim(), ParentPartId = r.ParentPartId, Description = T(r.Description, 4000),
            ListType = r.ListType, Status = PartsListStatus.Draft, Revision = SequflohFoundationService.FirstRevision(settings.Numbering.RevisionFormat) };
        db.SequflohPartsLists.Add(l);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return l;
    }

    public async Task UpdateListAsync(Guid id, PartsListInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, false, ct);
        Editable(l);
        if (r.RowVersion != 0) db.Entry(l).Property(x => x.RowVersion).OriginalValue = r.RowVersion;
        ModuleSupport.Required(r.Title, "Title", 300);
        await ValidateParentAsync(r.ParentPartId, ct);
        l.Title = r.Title.Trim(); l.ParentPartId = r.ParentPartId; l.Description = T(r.Description, 4000); l.ListType = r.ListType;
        await db.SaveChangesAsync(ct);
    }

    async Task<SequflohPartsListItem> BuildItemAsync(PartsListItemInput r, CancellationToken ct)
    {
        if (r.Quantity <= 0) throw new AppException("The quantity must be greater than zero.");
        string number, description, unit;
        if (r.PartId is { } pid)
        {
            var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new AppException("The part does not exist in the Part Master.");
            number = p.PartNumber; description = string.IsNullOrWhiteSpace(r.Description) ? p.Name : r.Description.Trim(); unit = string.IsNullOrWhiteSpace(r.Unit) ? p.UnitOfMeasure : r.Unit.Trim();
        }
        else
        {
            ModuleSupport.Required(r.PartNumber, "Part number", 60);
            ModuleSupport.Required(r.Description, "Description", 500);
            number = r.PartNumber!.Trim(); description = r.Description!.Trim(); unit = string.IsNullOrWhiteSpace(r.Unit) ? "EA" : r.Unit.Trim();
        }
        if (unit.Length > 20) throw new AppException("The unit is limited to 20 characters.");
        return new SequflohPartsListItem { PartId = r.PartId, PartNumber = number, Description = description.Length > 500 ? description[..500] : description, Quantity = r.Quantity, Unit = unit,
            Required = r.Required, AlternatePartNumber = T(r.AlternatePartNumber, 60), Traceability = r.Traceability, Notes = T(r.Notes, 2000) };
    }

    public async Task<SequflohPartsListItem> AddItemAsync(Guid id, PartsListItemInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        var item = await BuildItemAsync(r, ct);
        item.PartsListId = id; item.ItemNumber = (l.Items.Count == 0 ? 0 : l.Items.Max(i => i.ItemNumber)) + 1;
        db.SequflohPartsListItems.Add(item);
        l.UpdatedAt = m.Now;
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task UpdateItemAsync(Guid id, Guid itemId, PartsListItemInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        var item = l.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Parts list item");
        var n = await BuildItemAsync(r, ct);
        item.PartId = n.PartId; item.PartNumber = n.PartNumber; item.Description = n.Description; item.Quantity = n.Quantity; item.Unit = n.Unit; item.Required = n.Required;
        item.AlternatePartNumber = n.AlternatePartNumber; item.Traceability = n.Traceability; item.Notes = n.Notes;
        await db.SaveChangesAsync(ct);
    }

    public async Task DuplicateItemAsync(Guid id, Guid itemId, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        var s = l.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Parts list item");
        // the copy goes right after the source; later items move down one place
        foreach (var i in l.Items.Where(i => i.ItemNumber > s.ItemNumber)) i.ItemNumber++;
        db.SequflohPartsListItems.Add(new SequflohPartsListItem { PartsListId = id, ItemNumber = s.ItemNumber + 1, PartId = s.PartId, PartNumber = s.PartNumber, Description = s.Description,
            Quantity = s.Quantity, Unit = s.Unit, Required = s.Required, AlternatePartNumber = s.AlternatePartNumber, Traceability = s.Traceability, Notes = s.Notes });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveItemAsync(Guid id, Guid itemId, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        var item = l.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Parts list item");
        db.SequflohPartsListItems.Remove(item); // draft content only; released revisions are frozen in the revision history
        var k = 1;
        foreach (var i in l.Items.Where(i => i.Id != itemId).OrderBy(i => i.ItemNumber)) i.ItemNumber = k++;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderItemsAsync(Guid id, ReorderInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        if (r.Ids.Count != l.Items.Count || r.Ids.Distinct().Count() != l.Items.Count || r.Ids.Any(x => l.Items.All(i => i.Id != x))) throw new AppException("The new order must list every item exactly once.");
        // two passes avoid colliding with the unique (list, item number) index
        foreach (var i in l.Items) i.ItemNumber += 100000;
        await db.SaveChangesAsync(ct);
        for (var k = 0; k < r.Ids.Count; k++) l.Items.First(i => i.Id == r.Ids[k]).ItemNumber = k + 1;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CopyItemsAsync(Guid id, CopyItemsInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        if (r.SourceListId == id) throw new AppException("Choose a different list to copy from.");
        var l = await LoadListAsync(id, true, ct);
        Editable(l);
        var src = await db.SequflohPartsLists.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == r.SourceListId, ct) ?? throw new NotFoundException("Source parts list");
        var next = (l.Items.Count == 0 ? 0 : l.Items.Max(i => i.ItemNumber)) + 1;
        foreach (var s in src.Items.OrderBy(i => i.ItemNumber))
            db.SequflohPartsListItems.Add(new SequflohPartsListItem { PartsListId = id, ItemNumber = next++, PartId = s.PartId, PartNumber = s.PartNumber, Description = s.Description, Quantity = s.Quantity,
                Unit = s.Unit, Required = s.Required, AlternatePartNumber = s.AlternatePartNumber, Traceability = s.Traceability, Notes = s.Notes });
        await db.SaveChangesAsync(ct);
        return src.Items.Count;
    }

    public async Task<SequflohPartsList> DuplicateListAsync(Guid id, string? number, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var src = await db.SequflohPartsLists.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Parts list");
        var copy = await CreateListAsync(new PartsListInput($"Copy of {src.Title}".Length > 300 ? src.Title : $"Copy of {src.Title}", src.ParentPartId, src.Description, src.ListType, Number: number), ct);
        await CopyItemsAsync(copy.Id, new CopyItemsInput(id), ct);
        return copy;
    }

    public async Task ReleaseListAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, true, ct);
        if (l.Status != PartsListStatus.Draft) throw new ConflictException("Only a draft parts list can be released.");
        if (l.Items.Count == 0) throw new AppException("Add at least one item before releasing the parts list.");
        l.Status = PartsListStatus.Released; l.ReleasedById = m.UserId; l.ReleasedAt = m.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Freezes the current revision (header and items) in the revision history and opens the next revision as a draft.</summary>
    public async Task ReviseListAsync(Guid id, ReviseInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM sqf.sequfloh_parts_lists WHERE \"Id\" = {id} FOR UPDATE", ct);
        var l = await db.SequflohPartsLists.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Parts list");
        if (l.Status != PartsListStatus.Released) throw new ConflictException("Only a released parts list can be revised; edit the draft instead.");
        if (settings.WorkInstructions.RevisionReasonRequired && string.IsNullOrWhiteSpace(r.Reason)) throw new AppException("Enter the reason for the new revision.");
        db.SequflohPartsListRevisions.Add(new SequflohPartsListRevision { PartsListId = id, Revision = l.Revision, StatusAtRevision = l.Status, Reason = T(r.Reason, 2000), Snapshot = Snapshot(l) });
        l.Revision = SequflohFoundationService.NextRevision(l.Revision, settings.Numbering.RevisionFormat);
        l.Status = PartsListStatus.Draft; l.ReleasedAt = null; l.ReleasedById = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static string Snapshot(SequflohPartsList l) => JsonSerializer.Serialize(new
    {
        l.Number, l.Title, l.Revision, l.ParentPartId, l.Description, ListType = l.ListType.ToString(), l.ReleasedById, l.ReleasedAt,
        Items = l.Items.OrderBy(i => i.ItemNumber).Select(i => new { i.ItemNumber, i.PartId, i.PartNumber, i.Description, i.Quantity, i.Unit, i.Required, i.AlternatePartNumber, Traceability = i.Traceability.ToString(), i.Notes })
    }, SequflohJson.Options);

    public async Task SetListActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManagePartsLists, ct);
        var l = await LoadListAsync(id, false, ct);
        if (active)
        {
            if (l.Status != PartsListStatus.Inactive) throw new ConflictException("The parts list is already active.");
            l.Status = l.ReleasedAt is null ? PartsListStatus.Draft : PartsListStatus.Released;
        }
        else
        {
            if (l.Status == PartsListStatus.Inactive) throw new ConflictException("The parts list is already inactive.");
            l.Status = PartsListStatus.Inactive;
        }
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ operation steps
    void ValidateStep(OperationStepInput r)
    {
        ModuleSupport.Required(r.Title, "Title", 300);
        ModuleSupport.Required(r.DetailedAction, "Detailed action", 8000);
        if (r.Minimum is { } mn && r.Maximum is { } mx && mx < mn) throw new AppException("The maximum cannot be less than the minimum.");
        if (r.Target is { } t && ((r.Minimum is { } a && t < a) || (r.Maximum is { } b && t > b))) throw new AppException("The target must lie between the minimum and maximum.");
    }

    static void ApplyStep(SequflohOperationStep s, OperationStepInput r)
    {
        s.Title = r.Title.Trim(); s.Category = T(r.Category, 100); s.DetailedAction = r.DetailedAction.Trim(); s.DefaultTools = T(r.DefaultTools, 2000); s.DefaultParts = T(r.DefaultParts, 2000);
        s.Specification = T(r.Specification, 500); s.Target = r.Target; s.Minimum = r.Minimum; s.Maximum = r.Maximum; s.Unit = T(r.Unit, 20); s.SafetyWarning = T(r.SafetyWarning, 2000);
        s.QualityCheckpoint = T(r.QualityCheckpoint, 2000); s.ExpectedResult = T(r.ExpectedResult, 2000); s.EvidenceRequirement = T(r.EvidenceRequirement, 1000); s.RequiredRole = T(r.RequiredRole, 200);
        s.RequiredQualification = T(r.RequiredQualification, 200); s.IsActive = r.IsActive;
    }

    public async Task<SequflohOperationStep> CreateStepAsync(OperationStepInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManageOperationSteps, ct);
        ValidateStep(r);
        await f.RequireOptionAsync("operation-categories", r.Category, "Category", ct);
        var number = RecordNumbers.Clean(r.Number, 40, "operation step number");
        await RecordNumbers.EnsureFreeAsync(db.SequflohOperationSteps.Select(o => o.Number), number, "An operation step numbered", ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var s = new SequflohOperationStep { Number = number, DisplayOrder = (await db.SequflohOperationSteps.MaxAsync(x => (int?)x.DisplayOrder, ct) ?? 0) + 10 };
        ApplyStep(s, r);
        db.SequflohOperationSteps.Add(s);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return s;
    }

    public async Task UpdateStepAsync(Guid id, OperationStepInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManageOperationSteps, ct);
        ValidateStep(r);
        var s = await db.SequflohOperationSteps.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Operation step");
        if (r.RowVersion != 0) db.Entry(s).Property(x => x.RowVersion).OriginalValue = r.RowVersion;
        if (r.Category != s.Category) await f.RequireOptionAsync("operation-categories", r.Category, "Category", ct);
        ApplyStep(s, r);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SequflohOperationStep> DuplicateStepAsync(Guid id, string? number, CancellationToken ct)
    {
        var s = await db.SequflohOperationSteps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Operation step");
        return await CreateStepAsync(new OperationStepInput($"Copy of {s.Title}".Length > 300 ? s.Title : $"Copy of {s.Title}", s.Category, s.DetailedAction, s.DefaultTools, s.DefaultParts, s.Specification,
            s.Target, s.Minimum, s.Maximum, s.Unit, s.SafetyWarning, s.QualityCheckpoint, s.ExpectedResult, s.EvidenceRequirement, s.RequiredRole, s.RequiredQualification, s.IsActive, Number: number), ct);
    }

    public async Task SetStepActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManageOperationSteps, ct);
        var s = await db.SequflohOperationSteps.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Operation step");
        s.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderStepsAsync(ReorderInput r, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohManageOperationSteps, ct);
        var steps = await db.SequflohOperationSteps.ToListAsync(ct);
        if (r.Ids.Count != steps.Count || r.Ids.Distinct().Count() != steps.Count || r.Ids.Any(x => steps.All(s => s.Id != x))) throw new AppException("The new order must list every operation step exactly once.");
        for (var i = 0; i < r.Ids.Count; i++) steps.First(s => s.Id == r.Ids[i]).DisplayOrder = (i + 1) * 10;
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> HistoryAsync(string entityType, Guid id, CancellationToken ct) =>
        await db.AuditEntries.AsNoTracking().Where(a => a.EntityType == entityType && a.EntityId == id).OrderByDescending(a => a.OccurredAt)
            .Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Changes, a.Reason }).Take(300).ToListAsync(ct);

    // ------------------------------------------------------------------ a part's Parts list and Repair parts (links to other parts)
    public sealed record PartLinkInput(Guid? PartId, string? PartNumber, string? Description, decimal? Quantity = null, string? Notes = null);

    static string KindWords(PartLinkKind k) => k == PartLinkKind.RepairParts ? "Repair parts" : "Parts list";

    async Task<string> MeAsync(CancellationToken ct) => await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";

    /// <summary>The existing part (picked, or with exactly this number) or a new Repair part with the typed number; any part used this way is flagged Repair part.</summary>
    async Task<Part> ResolveRepairPartAsync(Part parent, PartLinkInput r, CancellationToken ct)
    {
        Part? p;
        if (r.PartId is { } pid) p = await db.Parts.FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new AppException("The part does not exist in the Part Master.");
        else
        {
            var pn = r.PartNumber?.Trim();
            if (string.IsNullOrEmpty(pn) || PartFields.IsBlank(pn)) throw new AppException("Pick a part or type its part number.");
            if (pn.Length > 60) throw new AppException("The part number is limited to 60 characters.");
            var up = pn.ToUpperInvariant();
            p = await db.Parts.FirstOrDefaultAsync(x => x.PartNumber.Trim().ToUpper() == up, ct);
            if (p is null)
            {
                var desc = r.Description?.Trim();
                if (desc is { Length: > 4000 }) throw new AppException("The description is limited to 4000 characters.");
                p = new Part { PartNumber = pn, Name = parent.Name, Description = string.IsNullOrEmpty(desc) ? null : desc, UnitOfMeasure = "Each", SiteId = parent.SiteId, DepartmentId = parent.DepartmentId,
                    Status = MasterStatus.Active, PartType = PartType.Component, IsRepairPart = true, TypeId = parent.TypeId, ProductFamily = parent.ProductFamily };
                db.Parts.Add(p);
                db.PartRevisions.Add(new PartRevision { PartId = p.Id, Revision = p.Revision, Reason = "Initial release (added as a repair part)", EffectiveOn = await m.TodayAsync(parent.SiteId, ct) });
                f.Audit("Create", "Part", p.Id, $"Created repair part {pn} while adding it to {parent.PartNumber}.", p.SiteId);
                return p;
            }
        }
        if (!p.IsRepairPart)
        {
            p.IsRepairPart = true;
            f.Audit("Update", "Part", p.Id, $"Marked {p.PartNumber} as a repair part while adding it to {parent.PartNumber}.", p.SiteId);
        }
        return p;
    }

    /// <summary>Links a part onto the parent's list once; returns false when it was already there.</summary>
    async Task<bool> LinkAsync(Part parent, Part child, PartLinkKind kind, string how, CancellationToken ct)
    {
        if (child.Id == parent.Id) throw new AppException("A part can't be on its own list.");
        if (await db.PartComponentLinks.AnyAsync(x => x.PartId == parent.Id && x.ComponentPartId == child.Id && x.Kind == kind, ct)) return false;
        db.PartComponentLinks.Add(new PartComponentLink { PartId = parent.Id, ComponentPartId = child.Id, Kind = kind, CreatedAt = m.Now, CreatedBy = await MeAsync(ct) });
        f.Audit("Link", "Part", parent.Id, $"Added {child.PartNumber} to the {KindWords(kind)} of {parent.PartNumber}{how}.", parent.SiteId);
        return true;
    }

    async Task<Part> EditablePartAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        if (!await m.CanAsync(Permissions.MasterDataEdit, p.SiteId, p.DepartmentId, ct) || !await m.CanAsync(Permissions.SequflohEditParts, p.SiteId, p.DepartmentId, ct)) throw new ForbiddenException();
        return p;
    }

    public async Task<object> LinksAsync(Guid id, PartLinkKind kind, CancellationToken ct)
    {
        var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await m.RequireAsync(Permissions.SequflohAccess, p.SiteId, p.DepartmentId, ct);
        return await (from l in db.PartComponentLinks.AsNoTracking() where l.PartId == id && l.Kind == kind
                      join c in db.Parts.AsNoTracking() on l.ComponentPartId equals c.Id
                      orderby c.PartNumber
                      select new { c.Id, c.PartNumber, c.Name, c.Description, Status = c.Status.ToString(), c.IsRepairPart, l.CreatedAt, l.CreatedBy }).ToListAsync(ct);
    }

    public async Task AddLinkAsync(Guid id, PartLinkKind kind, PartLinkInput r, CancellationToken ct)
    {
        var parent = await EditablePartAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var child = await ResolveRepairPartAsync(parent, r, ct);
        await LinkAsync(parent, child, kind, "", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveLinkAsync(Guid id, PartLinkKind kind, Guid componentId, CancellationToken ct)
    {
        var parent = await EditablePartAsync(id, ct);
        var l = await db.PartComponentLinks.FirstOrDefaultAsync(x => x.PartId == id && x.ComponentPartId == componentId && x.Kind == kind, ct) ?? throw new NotFoundException("Linked part");
        var pn = await db.Parts.Where(x => x.Id == componentId).Select(x => x.PartNumber).FirstOrDefaultAsync(ct);
        db.PartComponentLinks.Remove(l);
        f.Audit("Unlink", "Part", id, $"Removed {pn} from the {KindWords(kind)} of {parent.PartNumber}. The part stays in the Part Master.", parent.SiteId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A part replaced on a Repair work order: it is made a Repair part in the Part Master (created with the typed number when new), put on the repaired part's
    /// Repair parts list once, and added to the work order's Repair parts rows. Closed work orders can't be changed.
    /// </summary>
    public async Task AddWorkOrderRepairPartAsync(Guid recordId, PartLinkInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await db.SequflohTrackingRecords.FirstOrDefaultAsync(x => x.Id == recordId, ct) ?? throw new NotFoundException("Tracking record");
        await m.RequireAsync(Permissions.SequflohAccess, rec.SiteId, rec.DepartmentId, ct);
        if (rec.Type != TrackingType.Repair) throw new AppException("Repair parts are added on Repair work orders.");
        SequflohTrackingService.RequireOpen(rec);
        var parent = rec.PartId is { } ppid ? await db.Parts.FirstOrDefaultAsync(x => x.Id == ppid, ct) : null;
        if (parent is null) throw new AppException("Choose the part being repaired on this work order first.");
        if (r.Quantity is <= 0) throw new AppException("The quantity must be greater than zero.");
        var notes = r.Notes?.Trim(); if (notes is { Length: > 2000 }) throw new AppException("The notes are limited to 2000 characters.");
        var child = await ResolveRepairPartAsync(parent, r, ct);
        await LinkAsync(parent, child, PartLinkKind.RepairParts, $" from work order {rec.Number}", ct);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(rec.PartsRows)!.AsArray();
        if (rows.Any(x => (string?)x?["partId"] == child.Id.ToString() || string.Equals(((string?)x?["replacementPartNumber"])?.Trim(), child.PartNumber, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"Part {child.PartNumber} is already on this work order.");
        var desc = string.IsNullOrWhiteSpace(r.Description) ? child.Description ?? child.Name : r.Description.Trim();
        var row = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["partId"] = child.Id.ToString(), ["replacementPartNumber"] = child.PartNumber, ["description"] = desc.Length > 500 ? desc[..500] : desc,
            ["quantity"] = r.Quantity ?? 1, ["required"] = true, ["added"] = true,
        };
        if (!string.IsNullOrEmpty(notes)) row["notes"] = notes;
        rows.Add(row);
        rec.PartsRows = rows.ToJsonString();
        f.Audit("Update", "TrackingRecord", rec.Id, $"{rec.Number}: added repair part {child.PartNumber} (qty {r.Quantity ?? 1}).", rec.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Removes a row added on the work order; the part stays on the repaired part's Repair parts and in the Part Master.</summary>
    public async Task RemoveWorkOrderRepairPartAsync(Guid recordId, string rowId, CancellationToken ct)
    {
        var rec = await db.SequflohTrackingRecords.FirstOrDefaultAsync(x => x.Id == recordId, ct) ?? throw new NotFoundException("Tracking record");
        await m.RequireAsync(Permissions.SequflohAccess, rec.SiteId, rec.DepartmentId, ct);
        SequflohTrackingService.RequireOpen(rec);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(rec.PartsRows)!.AsArray();
        var row = rows.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(x => x["id"]?.ToString() == rowId) ?? throw new NotFoundException("Repair part row");
        if (row["added"]?.GetValue<bool>() != true) throw new AppException("Rows from the work instruction can't be removed.");
        rows.Remove(row);
        rec.PartsRows = rows.ToJsonString();
        f.Audit("Update", "TrackingRecord", rec.Id, $"{rec.Number}: removed repair part {row["replacementPartNumber"]} from the work order. It stays on the part's Repair parts.", rec.SiteId);
        await db.SaveChangesAsync(ct);
    }
}
