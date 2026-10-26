using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;
using Sequfloh.Application.Security;
using Sequfloh.Infrastructure.Modules.Quality;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record ProjectInput(string? Number, string? Name, string? Description = null, Guid? SiteId = null);
public sealed record ProjectLinkInput(Guid? ProjectId);

/// <summary>
/// Projects group work orders of any type and inspection requests. Anyone who can open SEQUFLOH can see them and pick one; creating, editing,
/// closing and deleting need the projects permission. The number is typed by the user and never generated. A project can be deleted only when nothing is linked.
/// </summary>
public sealed class SequflohProjectService(AppDbContext db, ModuleSupport m, SequflohFoundationService f, SequflohCrewService crew, SequflohTrackingService tracking)
{
    public const string EntityType = nameof(SequflohProject);
    static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? throw new AppException($"Keep it under {max} characters.") : s.Trim();

    async Task RequireViewAsync(CancellationToken ct) { if (!await m.CanAnyAsync(Permissions.SequflohAccess, ct)) throw new ForbiddenException(); }
    async Task RequireManageAsync(CancellationToken ct) { if (!await m.CanAnyAsync(Permissions.SequflohManageProjects, ct)) throw new ForbiddenException(); }

    async Task<SequflohProject> LoadAsync(Guid id, CancellationToken ct) =>
        await db.SequflohProjects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Project");

    /// <summary>Checks a project picked on a work order or inspection request: it must exist and be open. Returns the project or null.</summary>
    public async Task<SequflohProject?> RequireOpenAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } pid) return null;
        var p = await db.SequflohProjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new AppException("The project does not exist.");
        if (p.Status != ProjectStatus.Open) throw new AppException($"Project {p.Number} is closed.");
        return p;
    }

    public async Task<object> ListAsync(string? status, string? q, CancellationToken ct)
    {
        await RequireViewAsync(ct);
        var query = db.SequflohProjects.AsNoTracking();
        if (Enum.TryParse<ProjectStatus>(status, true, out var st)) query = query.Where(x => x.Status == st);
        if (!string.IsNullOrWhiteSpace(q)) { var like = $"%{q.Trim()}%"; query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.Name, like)); }
        var rows = await query.OrderBy(x => x.Status).ThenBy(x => x.Number).Take(1000).ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToList();
        var wos = await db.SequflohTrackingRecords.AsNoTracking().Where(r => r.ProjectId != null && ids.Contains(r.ProjectId.Value)).GroupBy(r => r.ProjectId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var reqs = await db.SequflohInspectionRequests.AsNoTracking().Where(r => r.ProjectId != null && ids.Contains(r.ProjectId.Value)).GroupBy(r => r.ProjectId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return rows.Select(x => new
        {
            x.Id, x.Number, x.Name, x.Description, x.SiteId, Site = x.SiteId is { } s && sites.TryGetValue(s, out var sn) ? sn : null, Status = x.Status.ToString(), x.CreatedAt,
            WorkOrders = wos.GetValueOrDefault(x.Id), InspectionRequests = reqs.GetValueOrDefault(x.Id),
        });
    }

    async Task FillAsync(SequflohProject p, ProjectInput i, CancellationToken ct)
    {
        var number = T(i.Number, 60) ?? throw new AppException("Enter the project number.");
        var name = T(i.Name, 200) ?? throw new AppException("Enter the project name.");
        if (await db.SequflohProjects.AnyAsync(x => x.Id != p.Id && x.Number.ToLower() == number.ToLower(), ct)) throw new ConflictException($"A project numbered {number} already exists.");
        if (i.SiteId is { } s && !await db.Sites.AnyAsync(x => x.Id == s, ct)) throw new AppException("Unknown site.");
        p.Number = number; p.Name = name; p.Description = T(i.Description, 2000); p.SiteId = i.SiteId;
    }

    static object Snapshot(SequflohProject p) => new { p.Number, p.Name, p.Description, p.SiteId, Status = p.Status.ToString() };

    public async Task<object> CreateAsync(ProjectInput i, CancellationToken ct)
    {
        await RequireManageAsync(ct);
        var p = new SequflohProject { Status = ProjectStatus.Open };
        await FillAsync(p, i, ct);
        db.SequflohProjects.Add(p);
        f.Audit("ProjectCreated", EntityType, p.Id, $"Project {p.Number} {p.Name} created", p.SiteId, context: new { before = (object?)null, after = Snapshot(p) });
        await db.SaveChangesAsync(ct);
        return new { p.Id, p.Number };
    }

    public async Task UpdateAsync(Guid id, ProjectInput i, CancellationToken ct)
    {
        await RequireManageAsync(ct);
        var p = await LoadAsync(id, ct);
        var before = Snapshot(p);
        await FillAsync(p, i, ct);
        f.Audit("ProjectUpdated", EntityType, p.Id, $"Project {p.Number} {p.Name} edited", p.SiteId, context: new { before, after = Snapshot(p) });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(Guid id, ProjectStatus status, CancellationToken ct)
    {
        await RequireManageAsync(ct);
        var p = await LoadAsync(id, ct);
        if (p.Status == status) return;
        p.Status = status;
        f.Audit(status == ProjectStatus.Closed ? "ProjectClosed" : "ProjectReopened", EntityType, p.Id, $"Project {p.Number} {(status == ProjectStatus.Closed ? "closed" : "reopened")}", p.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await RequireManageAsync(ct);
        var p = await LoadAsync(id, ct);
        var wos = await db.SequflohTrackingRecords.IgnoreQueryFilters().CountAsync(r => r.ProjectId == id, ct);
        var reqs = await db.SequflohInspectionRequests.CountAsync(r => r.ProjectId == id, ct);
        if (wos + reqs > 0) throw new ConflictException($"Project {p.Number} has {wos} work order(s) and {reqs} inspection request(s) linked. Close it instead, or unlink them first.");
        db.SequflohProjects.Remove(p);
        f.Audit("ProjectDeleted", EntityType, p.Id, $"Project {p.Number} {p.Name} deleted", p.SiteId, context: new { before = Snapshot(p), after = (object?)null });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Links a work order to a project (or unlinks it with null). Needs the projects permission or the permission to create that kind of work order.</summary>
    public async Task LinkWorkOrderAsync(Guid trackingId, Guid? projectId, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(trackingId, true, ct);
        if (!await m.CanAnyAsync(Permissions.SequflohManageProjects, ct) && !await m.CanAsync(SequflohTrackingService.CreatePermission(r.Type), r.SiteId, r.DepartmentId, ct)) throw new ForbiddenException();
        if (r.ProjectId == projectId) return;
        var p = await RequireOpenAsync(projectId, ct);
        var old = r.ProjectId is { } o ? await db.SequflohProjects.AsNoTracking().Where(x => x.Id == o).Select(x => x.Number).FirstOrDefaultAsync(ct) : null;
        r.ProjectId = projectId;
        f.Audit("ProjectLinked", SequflohTrackingService.EntityType, r.Id, p is null ? $"Work order {r.Number} removed from project {old}" : $"Work order {r.Number} linked to project {p.Number}" + (old is null ? "" : $" (was {old})"), r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Links an inspection request to a project (or unlinks it with null). Anyone who can open the request can do it.</summary>
    public async Task LinkRequestAsync(Guid requestId, Guid? projectId, CancellationToken ct)
    {
        var r = await db.SequflohInspectionRequests.FirstOrDefaultAsync(x => x.Id == requestId, ct) ?? throw new NotFoundException("Inspection request");
        if (!await m.CanAsync(Permissions.SequflohAccess, r.SiteId, r.DepartmentId, ct)) throw new NotFoundException("Inspection request");
        if (r.ProjectId == projectId) return;
        var p = await RequireOpenAsync(projectId, ct);
        var old = r.ProjectId is { } o ? await db.SequflohProjects.AsNoTracking().Where(x => x.Id == o).Select(x => x.Number).FirstOrDefaultAsync(ct) : null;
        r.ProjectId = projectId;
        f.Audit("ProjectLinked", SequflohInspectionService.EntityType, r.Id, p is null ? $"Inspection request {r.Number} removed from project {old}" : $"Inspection request {r.Number} linked to project {p.Number}" + (old is null ? "" : $" (was {old})"), r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    static decimal Hr(long s) => Math.Round(s / 3600m, 2);

    static string StatusText(TrackingStatus s) => s switch
    {
        TrackingStatus.NotStarted => "Not started", TrackingStatus.InProgress => "In progress", TrackingStatus.OnHold => "On hold", TrackingStatus.Canceled => "Cancelled", _ => s.ToString(),
    };

    static string StatusText(InspectionRequestStatus s) => s switch { InspectionRequestStatus.InProgress => "In progress", _ => s.ToString() };

    sealed record Rollup(List<SequflohTrackingRecord> Recs, List<object> Rows, long User, long CrewLabor, long DayWorkers, decimal Lines, int ChargebackReports, decimal ChargebackTotal);

    /// <summary>The project's work orders the user can see, each with its hours and costs, and the totals.</summary>
    async Task<Rollup> RollupAsync(Guid id, Dictionary<Guid, string>? fromRequest, CancellationToken ct)
    {
        var recs = await (await m.ScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(r => r.ProjectId == id).OrderBy(r => r.Number).ToListAsync(ct);
        var ids = recs.Select(r => r.Id).ToList();
        var cbs = await db.SequflohChargebackReports.AsNoTracking().Where(c => c.TrackingRecordId != null && ids.Contains(c.TrackingRecordId.Value)).ToListAsync(ct);
        var people = fromRequest is null ? [] : await NamesAsync(recs.Select(r => r.AssignedUserId), ct);
        var rows = new List<object>();
        long user = 0, crewLabor = 0, dayWorkers = 0; decimal lines = 0;
        foreach (var r in recs)
        {
            var t = await crew.TotalsAsync(r.Id, ct);
            var cost = (JsonSerializer.Deserialize<List<CostLine>>(r.CostLines, SequflohJson.Options) ?? []).Sum(l => l.Amount);
            user += t.UserSeconds; crewLabor += t.CrewLaborSeconds; dayWorkers += t.DayWorkerSeconds; lines += cost;
            if (fromRequest is null) continue;
            var cb = cbs.Where(c => c.TrackingRecordId == r.Id).Sum(c => SequflohChargebackService.TotalOf(c) ?? 0);
            rows.Add(new
            {
                r.Id, r.Number, r.Type, TypeLabel = SequflohPeriodSummary.TypeName(r.Type), r.Status, StatusLabel = StatusText(r.Status), r.PartNumber, r.QtyGood, r.QtyBad,
                Title = string.IsNullOrWhiteSpace(r.PartDescription) ? (string.IsNullOrWhiteSpace(r.WorkInstructionTitle) ? null : r.WorkInstructionTitle) : r.PartDescription,
                AssignedTo = r.AssignedUserId is { } a ? people.GetValueOrDefault(a) : null, r.UpdatedAt, FromRequest = fromRequest.GetValueOrDefault(r.Id),
                Hours = Hr(t.TotalSeconds), UserHours = Hr(t.UserSeconds), CrewHours = Hr(t.CrewLaborSeconds), CostLines = cost, Chargebacks = cb,
            });
        }
        return new(recs, rows, user, crewLabor, dayWorkers, lines, cbs.Count, cbs.Sum(c => SequflohChargebackService.TotalOf(c) ?? 0));
    }

    async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName, ct);
    }

    /// <summary>Open projects for SEQUFLOH Home: linked work orders and inspection requests, man hours and total cost, the same totals the project page shows.</summary>
    public async Task<object> OpenSummaryAsync(CancellationToken ct)
    {
        await RequireViewAsync(ct);
        var open = await db.SequflohProjects.AsNoTracking().Where(x => x.Status == ProjectStatus.Open).OrderBy(x => x.Number).Take(50).ToListAsync(ct);
        var reqScope = await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct);
        var result = new List<object>();
        foreach (var p in open)
        {
            var r = await RollupAsync(p.Id, null, ct);
            var reqs = await reqScope.CountAsync(x => x.ProjectId == p.Id, ct);
            result.Add(new { p.Id, p.Number, p.Name, WorkOrders = r.Recs.Count, InspectionRequests = reqs, Items = r.Recs.Count + reqs, Hours = Hr(r.User + r.CrewLabor), TotalCost = r.Lines + r.ChargebackTotal });
        }
        return result;
    }

    /// <summary>The project with every linked work order and inspection request and the totals: man hours (signed-in people, day workers, crew), costs, counts by type and quantities.
    /// Inspection requests carry no hours or costs, so a request turned into a work order is counted once, through the work order.</summary>
    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireViewAsync(ct);
        var p = await db.SequflohProjects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Project");
        var reqs = await (await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct)).Where(r => r.ProjectId == id).OrderBy(r => r.Number).ToListAsync(ct);
        var fromRequest = reqs.Where(r => r.WorkOrderId != null).GroupBy(r => r.WorkOrderId!.Value).ToDictionary(g => g.Key, g => g.First().Number);
        var x = await RollupAsync(id, fromRequest, ct);
        var recs = x.Recs;
        var woNumbers = recs.ToDictionary(r => r.Id, r => (r.Number, r.Status));
        return new
        {
            p.Id, p.Number, p.Name, p.Description, p.SiteId, Status = p.Status.ToString(), p.CreatedAt,
            CanManage = await m.CanAnyAsync(Permissions.SequflohManageProjects, ct),
            WorkOrders = x.Rows,
            InspectionRequests = reqs.Select(r => new
            {
                r.Id, r.Number, r.PartNumber, r.Status, StatusLabel = StatusText(r.Status), Title = r.PartDescription ?? r.Reason, AssignedTo = r.AssignedUserName, r.UpdatedAt,
                r.WorkOrderId, r.WorkOrderNumber, WorkOrderStatusLabel = r.WorkOrderId is { } w && woNumbers.TryGetValue(w, out var wo) ? StatusText(wo.Status) : null, r.Quantity,
            }),
            Totals = new
            {
                Hours = Hr(x.User + x.CrewLabor), UserHours = Hr(x.User), DayWorkerHours = Hr(x.DayWorkers), CrewHours = Hr(x.CrewLabor - x.DayWorkers),
                CostLines = x.Lines, ChargebackReports = x.ChargebackReports, ChargebackTotal = x.ChargebackTotal, TotalCost = x.Lines + x.ChargebackTotal,
                WorkOrders = recs.Count, InspectionRequests = reqs.Count,
                ByType = recs.GroupBy(r => SequflohPeriodSummary.TypeName(r.Type)).Select(g => new { Type = g.Key, Count = g.Count() }).OrderBy(x => x.Type),
                QtyGood = recs.Sum(r => r.QtyGood ?? 0), QtyBad = recs.Sum(r => r.QtyBad ?? 0),
            },
        };
    }
}
