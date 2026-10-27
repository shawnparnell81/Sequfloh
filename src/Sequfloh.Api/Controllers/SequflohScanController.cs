using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Barcode scans: finds the part for a scanned box label by exact number, and a part's latest activity.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh")]
public sealed class SequflohScanController(AppDbContext db, ModuleSupport m) : ControllerBase
{
    /// <summary>Other number fields a scan may match exactly, only when no main part number matches. OR # and cross references are never used.</summary>
    public static readonly string[] AlternateKeys = ["dmaPn", "jhPn"];
    static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase) { "/", "-", "--", "n/a", "na", "none", "tbd", "?", "0" };

    /// <summary>The number to look up from what the scanner sent. Label prefixes or suffixes can be removed here once the label layout is known.</summary>
    public static string? Normalize(string? raw)
    {
        var c = (raw ?? "").Trim();
        if (c.Length == 0 || c.Length > 200 || Placeholders.Contains(c)) return null;
        return c;
    }

    [HttpGet("scan")]
    public async Task<object> Scan([FromQuery] string? code, [FromQuery] string? only, CancellationToken ct)
    {
        var c = Normalize(code);
        var canAdd = await m.CanAnyAsync(Permissions.SequflohEditParts, ct);
        if (c is null) return new { Code = (code ?? "").Trim(), MatchedOn = (string?)null, Matches = Array.Empty<object>(), Records = Array.Empty<object>(), CanAdd = false };
        var scoped = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct);
        var upper = c.ToUpperInvariant();
        var hits = await scoped.Where(x => x.PartNumber.Trim().ToUpper() == upper).OrderBy(x => x.PartNumber).Take(20).ToListAsync(ct);
        var on = "Part number";
        if (hits.Count == 0 && !(only != "parts" && await RecordNumberExistsAsync(upper, ct)))
        {
            var dwg = await scoped.Where(x => x.DrawingNumber != null && x.DrawingNumber.Trim().ToUpper() == upper).Take(20).ToListAsync(ct);
            var like = "%" + c.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var maybe = await scoped.Where(x => x.AttributeText != null && EF.Functions.ILike(x.AttributeText, like)).Take(200).ToListAsync(ct);
            var alt = maybe.Where(x => { var a = PartAttributes.Read(x.Attributes); return AlternateKeys.Any(k => a.TryGetValue(k, out var v) && string.Equals(v?.Trim(), c, StringComparison.OrdinalIgnoreCase)); });
            hits = dwg.Concat(alt).DistinctBy(x => x.Id).OrderBy(x => x.PartNumber).ToList();
            on = "DWG #, DMA PN or JH PN";
        }
        // exact record numbers: work orders, inspection requests and projects (not for part fields)
        var records = new List<object>();
        if (only != "parts")
        {
            records.AddRange(await (await m.ScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.Number.Trim().ToUpper() == upper).Take(10)
                .Select(x => new { Kind = "Work order", x.Id, x.Number, Title = x.PartNumber, Link = "/sequfloh/record/" + x.Id }).ToListAsync(ct));
            records.AddRange(await (await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.Number.Trim().ToUpper() == upper).Take(10)
                .Select(x => new { Kind = "Inspection request", x.Id, x.Number, Title = x.PartNumber, Link = "/sequfloh/inspection-request/" + x.Id }).ToListAsync(ct));
            records.AddRange(await db.SequflohProjects.AsNoTracking().Where(x => x.Number.Trim().ToUpper() == upper).Take(10)
                .Select(x => new { Kind = "Project", x.Id, x.Number, Title = x.Name, Link = "/sequfloh/project/" + x.Id }).ToListAsync(ct));
        }
        return new
        {
            Records = records, Code = c, MatchedOn = hits.Count > 0 ? on : null, CanAdd = canAdd,
            Matches = hits.Select(x => new { x.Id, x.PartNumber, x.Name, x.Description, x.ProductFamily, Status = x.Status.ToString(), x.DrawingNumber }).ToList(),
        };
    }

    async Task<bool> RecordNumberExistsAsync(string upper, CancellationToken ct) =>
        await (await m.ScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct)).AnyAsync(x => x.Number.Trim().ToUpper() == upper, ct)
        || await (await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct)).AnyAsync(x => x.Number.Trim().ToUpper() == upper, ct)
        || await db.SequflohProjects.AsNoTracking().AnyAsync(x => x.Number.Trim().ToUpper() == upper, ct);

    /// <summary>The newest work orders (every type) and inspection requests for a part, newest first.</summary>
    [HttpGet("parts/{id:guid}/activity")]
    public async Task<object> Activity(Guid id, CancellationToken ct)
    {
        var p = await (await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct)).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new Application.Abstractions.NotFoundException("Part");
        var wos = await (await m.ScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.PartId == id).OrderByDescending(x => x.CreatedAt).Take(25)
            .Select(x => new { x.Id, x.Number, x.Type, x.Status, x.CreatedAt, x.SerialNumber }).ToListAsync(ct);
        var irs = await (await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => x.PartId == id).OrderByDescending(x => x.CreatedAt).Take(25)
            .Select(x => new { x.Id, x.Number, x.Status, x.CreatedAt, x.WorkOrderNumber }).ToListAsync(ct);
        var items = wos.Select(x => new { Kind = "WorkOrder", x.Id, x.Number, Type = SequflohTrackingService.Label(x.Type), Status = x.Status.ToString(), x.CreatedAt, Link = $"/sequfloh/record/{x.Id}", Detail = x.SerialNumber })
            .Concat(irs.Select(x => new { Kind = "InspectionRequest", x.Id, x.Number, Type = "Inspection request", Status = x.Status.ToString(), x.CreatedAt, Link = $"/sequfloh/inspection-request/{x.Id}", Detail = x.WorkOrderNumber }))
            .OrderByDescending(x => x.CreatedAt).Take(25).ToList();
        return new { Items = items, CanCreateRepair = p.IsRepairable && await m.CanAsync(Permissions.SequflohCreateRepairRecord, p.SiteId, p.DepartmentId, ct) };
    }
}
