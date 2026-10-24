using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>SEQUFLOH Parts and Items: shared Part Master view and execution attributes, parts lists, reusable operation steps, revision history.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh")]
public sealed class SequflohPartsController(AppDbContext db, ModuleSupport m, SequflohPartsService parts, SequflohPartsImport import, PartFamilyChangeService familyChange) : ControllerBase
{
    // ---------------- Part Master bulk import: read the file, preview, then confirm
    [HttpPost("parts/import/read"), RequestSizeLimit(20_000_000)]
    public async Task<PartsImportTable> ImportRead(IFormFile file, [FromQuery] bool allTables = false, [FromQuery] int? headerRow = null, CancellationToken ct = default)
    {
        await using var s = file.OpenReadStream();
        return await import.ReadAsync(s, file.FileName, allTables, ct, headerRow);
    }

    [HttpGet("parts/import/fields")]
    public object ImportFields() => SequflohPartsImport.Fields.Select(x => new { x.Key, x.Label, Choices = SequflohPartsImport.ChoiceKind(x.Key) });

    /// <summary>The fixed choices of the part form; Name, Product family and Part class come from the product families list.</summary>
    [HttpGet("parts/choices")]
    public object Choices() => new { PartChoices.Units, PartChoices.YesNo, PartChoices.LotControl };

    [HttpPost("parts/import/preview")]
    public Task<PartsImportPlan> ImportPreview(PartsImportRequest r, CancellationToken ct) => import.PreviewAsync(r, ct);

    [HttpPost("parts/import")]
    public Task<PartsImportPlan> Import(PartsImportRequest r, CancellationToken ct) => import.ImportAsync(r, ct);

    // ---------------- Part Master (shared parts table)
    async Task<IQueryable<Part>> FilteredAsync(string? q, string? view, string? family, MasterStatus? status, PartType? type, string? partType, string? sort, string? tag, CancellationToken ct)
    {
        var query = await parts.PartsQueryAsync(q, view, family, status, type, ct, partType);
        if (tag == "none") query = query.Where(x => x.StatusTag == null);
        else if (!string.IsNullOrWhiteSpace(tag)) query = query.Where(x => x.StatusTag == tag);
        var types = db.SequflohReferenceValues.Where(v => v.ListKey == SequflohCatalog.ProductFamilies);
        return sort switch
        {
            // parts without a type come last
            "typeName:asc" => query.OrderBy(x => types.Where(v => v.Id == x.TypeId).Select(v => v.Label).FirstOrDefault() ?? "\uffff").ThenBy(x => x.PartNumber),
            "typeName:desc" => query.OrderByDescending(x => types.Where(v => v.Id == x.TypeId).Select(v => v.Label).FirstOrDefault() ?? "").ThenBy(x => x.PartNumber),
            _ => SequflohPartsService.SortParts(query, sort)
        };
    }

    [HttpGet("parts")]
    public async Task<object> Parts([FromQuery] string? q, [FromQuery] string? view, [FromQuery] string? family, [FromQuery] MasterStatus? status, [FromQuery] PartType? type,
        [FromQuery] string? sort, [FromQuery] string? tag, [FromQuery] string? partType, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var query = await FilteredAsync(q, view, family, status, type, partType, sort, tag, ct);
        var types = db.SequflohReferenceValues.Where(v => v.ListKey == SequflohCatalog.ProductFamilies);
        return await Paging.PageAsync(query.Select(p => new
        {
            p.Id, p.PartNumber, p.Name, p.Description, p.Revision, p.StatusTag, p.StatusNote, p.ProductFamily, PartType = p.PartType.ToString(), p.AssemblyType, p.PartClass, p.IsAssembly, p.LotControl, p.UnitOfMeasure, Status = p.Status.ToString(), p.IsRepairable, p.IsRepairPart, p.IsSerialized, p.IsLotControlled,
            p.DrawingNumber, p.Specification, p.Notes, p.SiteId, p.UpdatedAt, p.TypeId, TypeName = types.Where(v => v.Id == p.TypeId).Select(v => v.Label).FirstOrDefault(), p.Attributes
        }), page, pageSize, ct);
    }

    /// <summary>The attribute fields (cross-reference numbers and others) shown on the part form, list, export and import.</summary>
    [HttpGet("parts/attributes")]
    public object Attributes() => PartAttributes.All.Select(a => new { a.Key, a.Label, a.MaxLength, a.Types, a.NotTypes });

    /// <summary>Every part that matches the filters as CSV, with the part type and all attribute columns.</summary>
    [HttpGet("parts/export")]
    public async Task<IActionResult> Export([FromQuery] string? q, [FromQuery] string? view, [FromQuery] string? family, [FromQuery] MasterStatus? status, [FromQuery] PartType? type,
        [FromQuery] string? sort, [FromQuery] string? tag, [FromQuery] string? partType, CancellationToken ct = default)
    {
        var query = await FilteredAsync(q, view, family, status, type, partType, sort, tag, ct);
        var types = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies).ToDictionaryAsync(v => v.Id, v => v.Label, ct);
        var rows = await query.Take(50_000).ToListAsync(ct);
        var head = new List<string> { "OR # (Part number)", "Name", "Description", "Product family" };
        head.AddRange(PartAttributes.All.Select(a => a.Label));
        head.AddRange(["Part class", "Assembly", "Unit of measure", "Repairable", "Repair part", "Lot controlled", "SENSEN # (Dwg #)", "Specification", "Status", "Official status in NetSuite"]);
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(",", head.Select(Csv))).Append("\r\n");
        foreach (var p in rows)
        {
            var attrs = PartAttributes.Read(p.Attributes);
            var cells = new List<string?> { p.PartNumber, p.Name, p.Description, p.TypeId is { } t && types.TryGetValue(t, out var tn) ? tn : p.ProductFamily };
            cells.AddRange(PartAttributes.All.Select(a => attrs.GetValueOrDefault(a.Key)));
            cells.AddRange([p.PartClass, p.IsAssembly ? "Yes" : "No", p.UnitOfMeasure, p.IsRepairable ? "Yes" : "No", p.IsRepairPart ? "Yes" : "No", p.LotControl ?? (p.IsLotControlled ? "Yes" : "No"),
                p.DrawingNumber, p.Specification, p.Status.ToString(), p.StatusTag]);
            sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        }
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", "parts.csv");
    }

    /// <summary>A CSV cell; values that a spreadsheet would run as a formula are prefixed with an apostrophe.</summary>
    static string Csv(string? v)
    {
        var s = v ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>Number of distinct OR # part numbers per product family (parts that share an OR # count once); parts without a family are counted under a null id.</summary>
    [HttpGet("parts/family-counts")]
    public async Task<object> FamilyCounts(CancellationToken ct) =>
        await (await parts.PartsQueryAsync(null, null, null, null, null, ct, null)).GroupBy(p => p.TypeId).Select(g => new { TypeId = g.Key, Count = g.Select(p => p.PartNumber).Distinct().Count() }).ToListAsync(ct);

    // ---------------- Change product family of many parts: preview, unmatched list, apply
    [HttpPost("parts/change-family/preview")]
    public Task<FamilyChangePreview> ChangeFamilyPreview(FamilyChangeRequest r, CancellationToken ct) => familyChange.PreviewAsync(r, ct);

    [HttpGet("parts/change-family/unmatched.xlsx")]
    public async Task<IActionResult> ChangeFamilyUnmatched([FromQuery] Guid sourceTypeId, CancellationToken ct) =>
        File(await familyChange.UnmatchedXlsxAsync(sourceTypeId, ct), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "parts-without-drawings.xlsx");

    [HttpPost("parts/change-family")]
    public Task<object> ChangeFamily(FamilyChangeRequest r, CancellationToken ct) => familyChange.ApplyAsync(r, ct);

    [HttpGet("parts/families")]
    public async Task<List<string>> Families(CancellationToken ct) =>
        await (await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(p => p.ProductFamily != null).Select(p => p.ProductFamily!).Distinct().OrderBy(x => x).ToListAsync(ct);

    [HttpGet("parts/{id:guid}")]
    public async Task<object> Part(Guid id, CancellationToken ct)
    {
        var p = await (await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct)).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new Application.Abstractions.NotFoundException("Part");
        string Name(Guid? u) => u is null ? "" : db.Users.Where(x => x.Id == u).Select(x => x.DisplayName).FirstOrDefault() ?? "";
        return new
        {
            Part = new
            {
                p.Id, p.PartNumber, p.Name, p.Description, p.StatusTag, p.StatusNote, PartType = p.PartType.ToString(), PartTypeValue = (int)p.PartType, p.Revision, p.UnitOfMeasure, p.ProductId, p.Material, p.HasSpecialCharacteristics, p.SiteId, p.DepartmentId, Status = p.Status.ToString(), StatusValue = (int)p.Status,
                p.ProductFamily, p.AssemblyType, p.PartClass, p.IsAssembly, p.LotControl, p.IsRepairable, p.IsRepairPart, p.IsSerialized, p.IsLotControlled, p.DrawingNumber, p.Specification, p.Notes, p.RowVersion, p.CreatedAt, p.UpdatedAt,
                p.TypeId, TypeName = await db.SequflohReferenceValues.Where(v => v.Id == p.TypeId).Select(v => v.Label).FirstOrDefaultAsync(ct), Attributes = PartAttributes.Read(p.Attributes),
                CreatedBy = Name(p.CreatedById), UpdatedBy = Name(p.UpdatedById)
            },
            Revisions = await db.PartRevisions.AsNoTracking().Where(r => r.PartId == id).OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Revision, r.Reason, r.EffectiveOn, r.CreatedAt }).ToListAsync(ct),
            PartsLists = await db.SequflohPartsLists.AsNoTracking().Where(l => l.ParentPartId == id || l.Items.Any(i => i.PartId == id)).OrderBy(l => l.Number)
                .Select(l => new { l.Id, l.Number, l.Title, l.Revision, l.Status, l.ListType }).ToListAsync(ct),
            Related = await RelatedAsync(id, ct),
            CanEdit = await m.CanAsync(Permissions.SequflohEditParts, p.SiteId, p.DepartmentId, ct),
            CanEditMaster = await m.CanAsync(Permissions.MasterDataEdit, p.SiteId, p.DepartmentId, ct)
        };
    }

    /// <summary>Records of SEQUFLOH that reference the part (work instructions and tracking records are added as those registers exist).</summary>
    private Task<object> RelatedAsync(Guid partId, CancellationToken ct) => SequflohRelated.ForPartAsync(db, partId, ct);

    [HttpPost("parts")] [RequirePermission(Permissions.SequflohEditParts)]
    public async Task<object> CreatePart(SqfPartInput r, CancellationToken ct) { var p = await parts.CreatePartAsync(r, ct); return new { p.Id, p.PartNumber }; }
    [HttpPut("parts/{id:guid}")] [RequirePermission(Permissions.SequflohEditParts)]
    public async Task<IActionResult> UpdatePart(Guid id, SqfPartInput r, CancellationToken ct) { await parts.UpdatePartAsync(id, r, ct); return NoContent(); }
    [HttpPut("parts/{id:guid}/execution")] [RequirePermission(Permissions.SequflohEditParts)]
    public async Task<IActionResult> Execution(Guid id, PartExecutionInput r, CancellationToken ct) { await parts.ApplyExecutionAsync(id, r, ct); return NoContent(); }
    [HttpPost("parts/{id:guid}/deactivate")] [RequirePermission(Permissions.SequflohEditParts)]
    public async Task<IActionResult> DeactivatePart(Guid id, CancellationToken ct) { await parts.SetPartActiveAsync(id, false, ct); return NoContent(); }
    [HttpPost("parts/{id:guid}/reactivate")] [RequirePermission(Permissions.SequflohEditParts)]
    public async Task<IActionResult> ReactivatePart(Guid id, CancellationToken ct) { await parts.SetPartActiveAsync(id, true, ct); return NoContent(); }
    [HttpGet("parts/{id:guid}/history")] [RequirePermission(Permissions.SequflohViewAuditTrail)] public async Task<object> PartHistory(Guid id, CancellationToken ct) { await Part(id, ct); return await parts.HistoryAsync(nameof(Domain.MasterData.Part), id, ct); }

    // ---------------- parts lists
    [HttpGet("parts-lists")]
    public async Task<object> Lists([FromQuery] string? q, [FromQuery] PartsListStatus? status, [FromQuery] PartsListType? type, [FromQuery] Guid? partId, [FromQuery] string? sort,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var query = db.SequflohPartsLists.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (type is not null) query = query.Where(x => x.ListType == type);
        if (partId is not null) query = query.Where(x => x.ParentPartId == partId);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.Description ?? ""), like) || x.Items.Any(i => EF.Functions.ILike(i.PartNumber, like))); }
        query = (sort ?? "updatedAt:desc") switch
        {
            "number:asc" => query.OrderBy(x => x.Number), "number:desc" => query.OrderByDescending(x => x.Number), "title:asc" => query.OrderBy(x => x.Title), "title:desc" => query.OrderByDescending(x => x.Title),
            "updatedAt:asc" => query.OrderBy(x => x.UpdatedAt), _ => query.OrderByDescending(x => x.UpdatedAt)
        };
        return await Paging.PageAsync(query.Select(x => new
        {
            x.Id, x.Number, x.Title, x.Revision, x.Status, x.ListType, x.UpdatedAt, Items = x.Items.Count,
            ParentPart = db.Parts.Where(p => p.Id == x.ParentPartId).Select(p => p.PartNumber).FirstOrDefault()
        }), page, pageSize, ct);
    }

    [HttpGet("parts-lists/{id:guid}")]
    public async Task<object> List(Guid id, CancellationToken ct)
    {
        var l = await db.SequflohPartsLists.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new Application.Abstractions.NotFoundException("Parts list");
        string Name(Guid? u) => u is null ? "" : db.Users.Where(x => x.Id == u).Select(x => x.DisplayName).FirstOrDefault() ?? "";
        return new
        {
            List = new { l.Id, l.Number, l.Title, l.ParentPartId, l.Revision, l.Status, l.Description, l.ListType, l.ReleasedAt, l.RowVersion, l.CreatedAt, l.UpdatedAt,
                ReleasedBy = Name(l.ReleasedById), CreatedBy = Name(l.CreatedById), UpdatedBy = Name(l.UpdatedById),
                ParentPart = await db.Parts.Where(p => p.Id == l.ParentPartId).Select(p => new { p.Id, p.PartNumber, p.Name, p.Revision }).FirstOrDefaultAsync(ct) },
            Items = l.Items.OrderBy(i => i.ItemNumber),
            Revisions = await db.SequflohPartsListRevisions.AsNoTracking().Where(r => r.PartsListId == id).OrderByDescending(r => r.CreatedAt)
                .Select(r => new { r.Id, r.Revision, r.StatusAtRevision, r.Reason, r.Snapshot, r.CreatedAt, CreatedBy = db.Users.Where(u => u.Id == r.CreatedById).Select(u => u.DisplayName).FirstOrDefault() }).ToListAsync(ct),
            CanEdit = await m.CanAnyAsync(Permissions.SequflohManagePartsLists, ct)
        };
    }

    [HttpPost("parts-lists")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<object> CreateList(PartsListInput r, CancellationToken ct) { var l = await parts.CreateListAsync(r, ct); return new { l.Id, l.Number }; }
    [HttpPut("parts-lists/{id:guid}")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> UpdateList(Guid id, PartsListInput r, CancellationToken ct) { await parts.UpdateListAsync(id, r, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/duplicate")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<object> DuplicateList(Guid id, CancellationToken ct) { var l = await parts.DuplicateListAsync(id, ct); return new { l.Id, l.Number }; }
    [HttpPost("parts-lists/{id:guid}/release")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> ReleaseList(Guid id, CancellationToken ct) { await parts.ReleaseListAsync(id, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/revise")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> ReviseList(Guid id, ReviseInput r, CancellationToken ct) { await parts.ReviseListAsync(id, r, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/deactivate")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> DeactivateList(Guid id, CancellationToken ct) { await parts.SetListActiveAsync(id, false, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/reactivate")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> ReactivateList(Guid id, CancellationToken ct) { await parts.SetListActiveAsync(id, true, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/items")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<object> AddItem(Guid id, PartsListItemInput r, CancellationToken ct) { var i = await parts.AddItemAsync(id, r, ct); return new { i.Id, i.ItemNumber }; }
    [HttpPut("parts-lists/{id:guid}/items/{itemId:guid}")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> UpdateItem(Guid id, Guid itemId, PartsListItemInput r, CancellationToken ct) { await parts.UpdateItemAsync(id, itemId, r, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/items/{itemId:guid}/duplicate")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> DuplicateItem(Guid id, Guid itemId, CancellationToken ct) { await parts.DuplicateItemAsync(id, itemId, ct); return NoContent(); }
    [HttpDelete("parts-lists/{id:guid}/items/{itemId:guid}")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId, CancellationToken ct) { await parts.RemoveItemAsync(id, itemId, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/items/reorder")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<IActionResult> ReorderItems(Guid id, ReorderInput r, CancellationToken ct) { await parts.ReorderItemsAsync(id, r, ct); return NoContent(); }
    [HttpPost("parts-lists/{id:guid}/copy-items")] [RequirePermission(Permissions.SequflohManagePartsLists)]
    public async Task<object> CopyItems(Guid id, CopyItemsInput r, CancellationToken ct) => new { Copied = await parts.CopyItemsAsync(id, r, ct) };
    [HttpGet("parts-lists/{id:guid}/history")] [RequirePermission(Permissions.SequflohViewAuditTrail)] public Task<object> ListHistory(Guid id, CancellationToken ct) => parts.HistoryAsync(nameof(SequflohPartsList), id, ct);

    // ---------------- operation steps
    [HttpGet("operation-steps")]
    public async Task<object> Steps([FromQuery] string? q, [FromQuery] string? category, [FromQuery] bool? active, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var query = db.SequflohOperationSteps.AsNoTracking();
        if (active is not null) query = query.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + x.DetailedAction + " " + (x.Category ?? ""), like)); }
        return await Paging.PageAsync(query.OrderBy(x => x.DisplayOrder).ThenBy(x => x.Number), page, pageSize, ct);
    }
    [HttpGet("operation-steps/{id:guid}")]
    public async Task<object> Step(Guid id, CancellationToken ct) => await db.SequflohOperationSteps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new Application.Abstractions.NotFoundException("Operation step");
    [HttpPost("operation-steps")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<object> CreateStep(OperationStepInput r, CancellationToken ct) { var s = await parts.CreateStepAsync(r, ct); return new { s.Id, s.Number }; }
    [HttpPut("operation-steps/{id:guid}")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<IActionResult> UpdateStep(Guid id, OperationStepInput r, CancellationToken ct) { await parts.UpdateStepAsync(id, r, ct); return NoContent(); }
    [HttpPost("operation-steps/{id:guid}/duplicate")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<object> DuplicateStep(Guid id, CancellationToken ct) { var s = await parts.DuplicateStepAsync(id, ct); return new { s.Id, s.Number }; }
    [HttpPost("operation-steps/{id:guid}/activate")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<IActionResult> ActivateStep(Guid id, CancellationToken ct) { await parts.SetStepActiveAsync(id, true, ct); return NoContent(); }
    [HttpPost("operation-steps/{id:guid}/deactivate")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<IActionResult> DeactivateStep(Guid id, CancellationToken ct) { await parts.SetStepActiveAsync(id, false, ct); return NoContent(); }
    [HttpPost("operation-steps/reorder")] [RequirePermission(Permissions.SequflohManageOperationSteps)]
    public async Task<IActionResult> ReorderSteps(ReorderInput r, CancellationToken ct) { await parts.ReorderStepsAsync(r, ct); return NoContent(); }
    [HttpGet("operation-steps/{id:guid}/history")] [RequirePermission(Permissions.SequflohViewAuditTrail)] public Task<object> StepHistory(Guid id, CancellationToken ct) => parts.HistoryAsync(nameof(SequflohOperationStep), id, ct);

    // ---------------- revision history across parts, parts lists and (later) work instructions
    [HttpGet("revision-history")]
    public async Task<object> RevisionHistory([FromQuery] string? q, [FromQuery] string? type, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        await SequflohRelated.RevisionHistoryAsync(db, await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct), q, type, page, pageSize, ct);
}
