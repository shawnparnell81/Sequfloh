using System.Text.Json;
using Sequfloh.Application.Security;
using Sequfloh.Domain.MasterData;
using Sequfloh.Infrastructure.Modules.MasterData;
using Sequfloh.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record InspectionRequestInput(Guid SiteId, string? PartNumber = null, Guid? PartId = null, string? PartDescription = null, decimal? Quantity = null, string? Reason = null,
    string? Requirement = null, Guid? WorkInstructionId = null, DateOnly? RequestedDate = null, string? Priority = null, string? Number = null);
public sealed record InspectionConvertInput(Guid? WorkInstructionId = null, TrackingType Type = TrackingType.Inspection, string? Number = null);

/// <summary>
/// Inspection requests. Anyone who can open SEQUFLOH at a site can submit one. The list is ordered by priority (the order of the admin list) and then by
/// requested completion date. A request can be turned into a work order that carries its part, quantity, priority, date and requirement.
/// </summary>
public sealed class SequflohInspectionService(AppDbContext db, ModuleSupport m, SequflohFoundationService f, SequflohTrackingService tracking)
{
    public const string EntityType = nameof(SequflohInspectionRequest);
    static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? throw new AppException($"Keep it under {max} characters.") : s.Trim();

    /// <summary>Queue position of a work order priority: urgent / critical first, then high, normal, low.</summary>
    public static int WorkOrderRank(string? p) => p switch { "Critical" or "Urgent" => 0, "High" => 1, "Low" => 3, _ => 2 };

    async Task<IQueryable<SequflohInspectionRequest>> ScopeAsync(CancellationToken ct) => await m.ScopeAsync(db.SequflohInspectionRequests.AsNoTracking(), Permissions.SequflohAccess, ct);

    async Task<SequflohInspectionRequest> LoadAsync(Guid id, CancellationToken ct)
    {
        var r = await db.SequflohInspectionRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Inspection request");
        if (!await m.CanAsync(Permissions.SequflohAccess, r.SiteId, r.DepartmentId, ct)) throw new NotFoundException("Inspection request");
        return r;
    }

    public async Task<object> ListAsync(string? status, string? q, CancellationToken ct)
    {
        var query = await ScopeAsync(ct);
        if (status == "active") query = query.Where(x => x.Status == InspectionRequestStatus.Open || x.Status == InspectionRequestStatus.InProgress);
        else if (Enum.TryParse<InspectionRequestStatus>(status, true, out var st)) query = query.Where(x => x.Status == st);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Number + " " + x.PartNumber + " " + (x.PartDescription ?? "") + " " + (x.Reason ?? "") + " " + (x.RequestedByName ?? ""), like));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
        var ranks = await f.OptionsAsync("inspection-priorities", ct);
        int Rank(string p) { var i = ranks.FindIndex(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)); return i < 0 ? ranks.Count : i; }
        var today = await m.TodayAsync(null, ct);
        return rows.OrderBy(x => x.Status is InspectionRequestStatus.Done or InspectionRequestStatus.Cancelled ? 1 : 0).ThenBy(x => Rank(x.Priority))
            .ThenBy(x => x.RequestedDate is null).ThenBy(x => x.RequestedDate).ThenBy(x => x.CreatedAt)
            .Select(x => Dto(x, today)).ToList();
    }

    static object Dto(SequflohInspectionRequest x, DateOnly today) => new
    {
        x.Id, x.Number, x.SiteId, x.PartId, x.PartNumber, x.PartDescription, x.Quantity, x.Reason, x.Requirement, x.WorkInstructionId, x.WorkInstructionLabel, x.RequestedDate,
        x.Priority, x.Status, x.RequestedByName, x.CreatedAt, x.WorkOrderId, x.WorkOrderNumber,
        Overdue = x.RequestedDate is { } d && d < today && x.Status is InspectionRequestStatus.Open or InspectionRequestStatus.InProgress,
    };

    public async Task<object> GetAsync(Guid id, CancellationToken ct) => Dto(await LoadAsync(id, ct), await m.TodayAsync(null, ct));

    async Task Fill(SequflohInspectionRequest r, InspectionRequestInput x, CancellationToken ct)
    {
        var pn = T(x.PartNumber, 100);
        string? desc = T(x.PartDescription, 300);
        Guid? partId = null;
        if (x.PartId is { } pid)
        {
            var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(e => e.Id == pid, ct) ?? throw new AppException("The part does not exist in the Part Master.");
            partId = p.Id; pn = p.PartNumber; desc ??= p.Name;
        }
        else if (pn is not null)
        {
            // free entry: link it when the number matches a part
            // linked only when one part has the number; with several (another Position), the request keeps the typed number
            var ps = await db.Parts.AsNoTracking().Where(e => e.PartNumber == pn).Select(e => new { e.Id, e.Name }).Take(2).ToListAsync(ct);
            if (ps.Count == 1) { partId = ps[0].Id; desc ??= ps[0].Name; }
        }
        if (pn is null) throw new AppException("Enter the part number.");
        if (x.Quantity is < 0) throw new AppException("The quantity can't be negative.");
        var reason = T(x.Reason, 100);
        if (reason is not null) await f.RequireOptionAsync("inspection-reasons", reason, "Reason for inspection", ct);
        var priority = T(x.Priority, 50) ?? "Normal";
        await f.RequireOptionAsync("inspection-priorities", priority, "Priority", ct);
        string? wiLabel = null;
        if (x.WorkInstructionId is { } wid)
        {
            var wi = await db.SequflohWorkInstructions.AsNoTracking().Where(e => e.Id == wid).Select(e => new { e.Number, e.Revision, e.Title }).FirstOrDefaultAsync(ct)
                ?? throw new AppException("The work instruction does not exist.");
            wiLabel = $"{wi.Number} rev {wi.Revision} {wi.Title}";
        }
        r.PartId = partId; r.PartNumber = pn; r.PartDescription = desc; r.Quantity = x.Quantity; r.Reason = reason; r.Requirement = T(x.Requirement, 4000);
        r.WorkInstructionId = x.WorkInstructionId; r.WorkInstructionLabel = wiLabel; r.RequestedDate = x.RequestedDate; r.Priority = priority;
    }

    /// <summary>"INSP-{family}{yy}-" from Tracking settings and the part's product family, or null when either has no code.</summary>
    async Task<string?> PrefixAsync(Guid? partId, Guid siteId, CancellationToken ct)
    {
        if (partId is null) return null;
        var code = SequflohTrackingService.CleanCodeOrNull((await f.GetSettingsAsync(ct)).Tracking.InspectionRequestNumberCode);
        return code is null ? null : await tracking.FamilyPrefixAsync($"{code}-", null, partId, siteId, ct);
    }

    async Task<string> NextAsync(string prefix, bool lockPrefix, CancellationToken ct)
    {
        if (lockPrefix) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [prefix], ct);
        var taken = await db.SequflohInspectionRequests.IgnoreQueryFilters().AsNoTracking().Where(x => x.Number.StartsWith(prefix)).Select(x => x.Number).ToListAsync(ct);
        var max = taken.Select(n => n[prefix.Length..]).Where(s => s.Length > 0 && s.All(char.IsAsciiDigit)).Select(s => int.TryParse(s, out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:000}";
    }

    /// <summary>The number a new request for this part would get, or null when it has to be typed.</summary>
    public async Task<object> NextNumberAsync(Guid? partId, string? partNumber, Guid siteId, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohAccess, siteId, null, ct);
        if (partId is null && T(partNumber, 100) is { } pn)
        {
            var ps = await db.Parts.AsNoTracking().Where(e => e.PartNumber == pn).Select(e => e.Id).Take(2).ToListAsync(ct);
            if (ps.Count == 1) partId = ps[0];
        }
        return new { number = await PrefixAsync(partId, siteId, ct) is { } p ? await NextAsync(p, false, ct) : null };
    }

    public async Task<object> CreateAsync(InspectionRequestInput x, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohAccess, x.SiteId, null, ct);
        await m.ValidateLocationAsync(x.SiteId, null, ct);
        var r = new SequflohInspectionRequest { SiteId = x.SiteId, RequestedById = m.UserId };
        await Fill(r, x, ct);
        r.RequestedByName = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (string.IsNullOrWhiteSpace(x.Number) && await PrefixAsync(r.PartId, r.SiteId, ct) is { } prefix) r.Number = await NextAsync(prefix, true, ct);
        else r.Number = RecordNumbers.Clean(x.Number, 40, "inspection request number");
        await RecordNumbers.EnsureFreeAsync(db.SequflohInspectionRequests.Select(o => o.Number), r.Number, "An inspection request numbered", ct);
        db.SequflohInspectionRequests.Add(r);
        f.Audit("InspectionRequestCreated", EntityType, r.Id, $"{r.Number} requested for {r.PartNumber} ({r.Priority})", r.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(r, await m.TodayAsync(null, ct));
    }

    public async Task<object> UpdateAsync(Guid id, InspectionRequestInput x, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        await Fill(r, x, ct);
        await db.SaveChangesAsync(ct);
        return Dto(r, await m.TodayAsync(null, ct));
    }

    public async Task<object> SetStatusAsync(Guid id, InspectionRequestStatus status, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        if (r.Status != status)
        {
            f.Audit("InspectionRequestStatus", EntityType, r.Id, $"{r.Number} status {r.Status} → {status}", r.SiteId);
            r.Status = status;
            await db.SaveChangesAsync(ct);
        }
        return Dto(r, await m.TodayAsync(null, ct));
    }

    /// <summary>Makes a work order from the request. The part's drawings are attached as they are now.</summary>
    public async Task<object> ConvertAsync(Guid id, InspectionConvertInput x, CancellationToken ct)
    {
        var r = await LoadAsync(id, ct);
        if (r.WorkOrderId is not null) throw new ConflictException($"{r.Number} already has work order {r.WorkOrderNumber}.");
        if (r.Status == InspectionRequestStatus.Cancelled) throw new AppException("A cancelled request can't be turned into a work order.");
        var woPriorities = await f.OptionsAsync("priorities", ct);
        var priority = woPriorities.Contains(r.Priority) ? r.Priority : WorkOrderRank(r.Priority) switch { 0 => woPriorities.FirstOrDefault(p => WorkOrderRank(p) == 0) ?? "High", 1 => "High", 3 => "Low", _ => "Normal" };
        if (!woPriorities.Contains(priority)) priority = woPriorities.Contains("Normal") ? "Normal" : woPriorities.First();
        DateTimeOffset? due = null;
        if (r.RequestedDate is { } d)
        {
            var (_, end) = await m.DayRangeAsync(r.SiteId, d, d, ct);
            due = end;
        }
        var cond = string.Join("\n", new[] { $"Inspection request {r.Number}", r.Reason is null ? null : $"Reason: {r.Reason}", r.Requirement is null ? null : $"Requirement: {r.Requirement}" }.Where(s => s is not null));
        var rec = await tracking.CreateAsync(new TrackingCreateInput(x.Type, x.WorkInstructionId, r.SiteId, r.DepartmentId, r.PartId, Priority: priority, DueAt: due, ReportedCondition: cond, Number: x.Number), ct);
        var tracked = await db.SequflohTrackingRecords.FirstAsync(e => e.Id == rec.Id, ct);
        if (r.Quantity is { } q) tracked.QuantityTarget = (int)Math.Round(q);
        if (x.Type != TrackingType.Validation && rec.PartId is { } part && !await db.TrackingRecordDrawings.AnyAsync(e => e.TrackingRecordId == rec.Id, ct))
            await Sequfloh.Drawings.DrawingService.AttachToRecordAsync(db, rec.Id, part, ct);
        r.WorkOrderId = rec.Id; r.WorkOrderNumber = rec.Number;
        if (r.Status == InspectionRequestStatus.Open) r.Status = InspectionRequestStatus.InProgress;
        f.Audit("InspectionRequestConverted", EntityType, r.Id, $"{r.Number} turned into work order {rec.Number}", r.SiteId);
        f.Audit("TrackingRecordFromRequest", SequflohTrackingService.EntityType, rec.Id, $"{rec.Number} made from inspection request {r.Number}", r.SiteId);
        await db.SaveChangesAsync(ct);
        return new { workOrderId = rec.Id, workOrderNumber = rec.Number };
    }

    public async Task<object> HistoryAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        return await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id).OrderByDescending(a => a.OccurredAt)
            .Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Changes, a.Reason }).Take(500).ToListAsync(ct);
    }
}
