using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>SEQUFLOH reports, Export Center, audit trail views and module search.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh")]
public sealed class SequflohReportsController(AppDbContext db, ModuleSupport m, SequflohReportService reports, SequflohPrintService print, SequflohInstructionService instructions,
    SequflohTrackingService tracking) : ControllerBase
{
    static readonly Dictionary<string, string> AuditViews = new()
    {
        ["system"] = "System Audit Events", ["instructions"] = "Work Instruction History", ["access"] = "User Access History", ["changes"] = "Record Change History", ["exports"] = "Export History",
    };

    async Task<List<(string, string)>> LabelsAsync(SequflohReportFilter p, CancellationToken ct)
    {
        var l = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(p.Q)) l.Add(("Search", p.Q.Trim()));
        if (p.SiteId is { } s) l.Add(("Site", await db.Sites.AsNoTracking().Where(x => x.Id == s).Select(x => x.Code).FirstOrDefaultAsync(ct) ?? s.ToString()));
        if (!string.IsNullOrWhiteSpace(p.Status)) l.Add(("Status", p.Status));
        if (!string.IsNullOrWhiteSpace(p.Type)) l.Add(("Type", p.Type));
        if (!string.IsNullOrWhiteSpace(p.Action)) l.Add(("Action", p.Action));
        if (p.UserId is { } u) l.Add(("User", await db.Users.AsNoTracking().Where(x => x.Id == u).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) ?? u.ToString()));
        if (p.From is { } f) l.Add(("From", f.ToString("yyyy-MM-dd")));
        if (p.To is { } t) l.Add(("To", t.ToString("yyyy-MM-dd")));
        return l;
    }

    // ------------------------------------------------------------------ reports and Export Center
    [HttpGet("reports")] [RequirePermission(Permissions.SequflohViewReports)]
    public async Task<object> Catalog(CancellationToken ct) => new { Reports = await reports.AvailableAsync(ct), CanExport = await reports.CanExportAsync(ct) };

    /// <summary>One report: the requested page of the full filtered result, the total, and status counts.</summary>
    [HttpGet("reports/{key}")] [RequirePermission(Permissions.SequflohViewReports)]
    public async Task<object> Run(string key, [FromQuery] string? q, [FromQuery] Guid? site, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] Guid? user,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? action, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var r = await reports.RunAsync(key, new(q, site, status, type, user, from, to, action), SequflohReportService.ExportLimit, ct);
        pageSize = Math.Clamp(pageSize, 1, 200); page = Math.Max(1, page);
        return new
        {
            Key = key, r.Definition.Title, r.Definition.Description, r.Columns, Total = r.Rows.Count, Items = r.Rows.Skip((page - 1) * pageSize).Take(pageSize),
            StatusCounts = key is "task-performance" or "validation" or "fai" or "repair" or "production" ? SequflohReportService.StatusCounts(r) : null,
        };
    }

    /// <summary>Excel workbook of the full filtered result (not just the visible page), with the report title block. Audited as an export.</summary>
    [HttpGet("reports/{key}/xlsx")] [RequirePermission(Permissions.SequflohViewReports)]
    public async Task<IActionResult> Xlsx(string key, [FromQuery] string? q, [FromQuery] Guid? site, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] Guid? user,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? action, [FromQuery] string? fileName, CancellationToken ct = default)
    {
        var p = new SequflohReportFilter(q, site, status, type, user, from, to, action);
        var (bytes, name) = await reports.ExportAsync(key, p, await LabelsAsync(p, ct), fileName, ct);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    // ------------------------------------------------------------------ audit trail views
    /// <summary>One of the five SEQUFLOH audit views over the shared audit trail (read-only: audit events cannot be edited or deleted).</summary>
    [HttpGet("audit/{view}")] [RequirePermission(Permissions.SequflohViewAuditTrail)]
    public async Task<object> Audit(string view, [FromQuery] string? q, [FromQuery] Guid? user, [FromQuery] string? action, [FromQuery] string? entityType, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (!AuditViews.TryGetValue(view, out var title)) throw new NotFoundException("Audit view");
        var query = await reports.FilterAuditAsync(SequflohReportService.AuditView(await reports.AuditQueryAsync(ct), view), new(q, null, null, entityType, user, from, to, action), ct);
        query = sort switch
        {
            "occurredAt:asc" => query.OrderBy(a => a.OccurredAt), "user:asc" => query.OrderBy(a => a.UserName).ThenByDescending(a => a.OccurredAt), "user:desc" => query.OrderByDescending(a => a.UserName).ThenByDescending(a => a.OccurredAt),
            "action:asc" => query.OrderBy(a => a.Action).ThenByDescending(a => a.OccurredAt), "action:desc" => query.OrderByDescending(a => a.Action).ThenByDescending(a => a.OccurredAt),
            _ => query.OrderByDescending(a => a.OccurredAt),
        };
        var paged = await Paging.PageAsync(query.Select(a => new { a.Id, a.OccurredAt, a.UserName, a.Action, a.EntityType, a.EntityId, a.Description, a.Reason }), page, pageSize, ct);
        var actions = await SequflohReportService.AuditView(await reports.AuditQueryAsync(ct), view).Select(a => a.Action).Distinct().OrderBy(a => a).Take(200).ToListAsync(ct);
        return new { Title = title, paged.Items, paged.Total, Actions = actions };
    }

    [HttpGet("audit/entry/{id:long}")] [RequirePermission(Permissions.SequflohViewAuditTrail)]
    public async Task<object> AuditEntry(long id, CancellationToken ct)
    {
        var a = await (await reports.AuditQueryAsync(ct)).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Audit event");
        var site = a.SiteId is null ? null : await db.Sites.AsNoTracking().Where(s => s.Id == a.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefaultAsync(ct);
        var path = a.EntityType switch
        {
            SequflohInstructionService.EntityType => $"/sequfloh/instruction/{a.EntityId}", SequflohTrackingService.EntityType => $"/sequfloh/record/{a.EntityId}",
            nameof(SequflohPartsList) => $"/sequfloh/parts-lists/{a.EntityId}", _ => null,
        };
        return new { a.Id, a.OccurredAt, a.UserName, a.Action, a.EntityType, a.EntityId, a.Description, a.Reason, a.Changes, a.CorrelationId, Site = site, Path = path };
    }

    /// <summary>Printable audit record summary of one view and its filters, from the application's PDF renderer.</summary>
    [HttpGet("audit/{view}/print")] [RequirePermission(Permissions.SequflohViewAuditTrail)]
    public async Task<IActionResult> AuditPrint(string view, [FromQuery] string? q, [FromQuery] Guid? user, [FromQuery] string? action, [FromQuery] string? entityType, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, CancellationToken ct = default)
    {
        if (!AuditViews.TryGetValue(view, out var title)) throw new NotFoundException("Audit view");
        var p = new SequflohReportFilter(q, null, null, entityType, user, from, to, action);
        var rows = await (await reports.FilterAuditAsync(SequflohReportService.AuditView(await reports.AuditQueryAsync(ct), view), p, ct)).OrderByDescending(a => a.OccurredAt).Take(2000).ToListAsync(ct);
        var (pdf, name) = await print.AuditSummaryAsync(title, rows, await LabelsAsync(p, ct), ct);
        Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
        return File(pdf, "application/pdf");
    }

    // ------------------------------------------------------------------ module search
    /// <summary>SEQUFLOH search across work instructions, parts, parts lists, tracking records, tasks and (for user administrators) users; only permitted records.</summary>
    [HttpGet("search")]
    public async Task<object> Search([FromQuery] string q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return new { Groups = Array.Empty<object>() };
        var like = $"%{q}%";
        const int n = 8;
        var groups = new List<object>();
        var wis = await (await instructions.QueryAsync(ct)).Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.PartNumber ?? ""), like)).OrderBy(x => x.Number).ThenByDescending(x => x.Revision).Take(n)
            .Select(x => new { x.Id, Number = x.Number + " rev " + x.Revision, x.Title, Status = x.Status.ToString(), Path = "/sequfloh/instruction/" + x.Id }).ToListAsync(ct);
        groups.Add(new { Key = "instructions", Label = "Work instructions", Items = wis });
        var parts = await (await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(x => EF.Functions.ILike(x.PartNumber + " " + x.Name, like)).OrderBy(x => x.PartNumber).Take(n)
            .Select(x => new { x.Id, Number = x.PartNumber + " rev " + x.Revision, Title = x.Name, Status = x.Status.ToString(), Path = "/sequfloh/parts/" + x.Id }).ToListAsync(ct);
        groups.Add(new { Key = "parts", Label = "Parts", Items = parts });
        var lists = await db.SequflohPartsLists.AsNoTracking().Where(x => EF.Functions.ILike(x.Number + " " + x.Title, like)).OrderBy(x => x.Number).Take(n)
            .Select(x => new { x.Id, Number = x.Number + " rev " + x.Revision, x.Title, Status = x.Status.ToString(), Path = "/sequfloh/parts-lists/" + x.Id }).ToListAsync(ct);
        groups.Add(new { Key = "parts-lists", Label = "Parts lists", Items = lists });
        var tr = (await tracking.QueryAsync(ct)).Where(x => EF.Functions.ILike(x.Number + " " + x.WorkInstructionNumber + " " + (x.PartNumber ?? "") + " " + (x.SerialNumber ?? "") + " " + (x.WorkOrder ?? ""), like));
        var recs = await tr.Where(x => x.Type != TrackingType.Production).OrderByDescending(x => x.CreatedAt).Take(n)
            .Select(x => new { x.Id, x.Number, Title = x.WorkInstructionNumber + " rev " + x.WorkInstructionRevision + " " + x.WorkInstructionTitle, Status = x.Status.ToString(), Path = "/sequfloh/record/" + x.Id }).ToListAsync(ct);
        groups.Add(new { Key = "records", Label = "Tracking records", Items = recs });
        var tasks = await tr.Where(x => x.Type == TrackingType.Production).OrderByDescending(x => x.CreatedAt).Take(n)
            .Select(x => new { x.Id, x.Number, Title = (x.Operation ?? x.WorkInstructionTitle) + (x.WorkOrder == null ? "" : " · " + x.WorkOrder), Status = x.Status.ToString(), Path = "/sequfloh/record/" + x.Id }).ToListAsync(ct);
        groups.Add(new { Key = "tasks", Label = "Tasks", Items = tasks });
        if (await m.CanAnyAsync(Permissions.SequflohManageModuleAccess, ct) || await m.CanAnyAsync(Permissions.AdminUsers, ct))
        {
            var users = await db.Users.AsNoTracking().Where(u => EF.Functions.ILike(u.DisplayName + " " + (u.Email ?? ""), like)).OrderBy(u => u.DisplayName).Take(n)
                .Select(u => new { u.Id, Number = u.DisplayName, Title = u.Email ?? "", Status = u.IsActive ? "Active" : "Inactive", Path = "/sequfloh/admin/module-access" }).ToListAsync(ct);
            groups.Add(new { Key = "users", Label = "Users", Items = users });
        }
        return new { Groups = groups };
    }
}
