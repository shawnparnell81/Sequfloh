using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Reports;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record SequflohReportFilter(string? Q, Guid? SiteId, string? Status, string? Type, Guid? UserId, DateOnly? From, DateOnly? To, string? Action = null);
public sealed record SequflohReportInfo(string Key, string Title, string Description, bool Report, bool NeedsAudit);

/// <summary>
/// SEQUFLOH operational reports and the Export Center. Every dataset is read through the module scope, so rows outside the
/// user's sites never appear on screen or in a workbook; values come only from stored records. Exports reuse the shared Excel writer and are audited.
/// </summary>
public sealed class SequflohReportService(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking, SequflohInstructionService instructions, SequflohFoundationService f,
    Sequfloh.Application.Security.IAccessService access, ICurrentUser current)
{
    public const int ExportLimit = 50_000;

    public static readonly SequflohReportInfo[] Catalog =
    [
        new("task-performance", "Task Performance", "Task counts by status with assigned users, parts, operations, planned and actual dates, benchmark ranges, elapsed time and variance.", true, false),
        new("validation", "Validation Status", "Validation records with instruction revision, part, owner, dates, result, review and approval status and deviations.", true, false),
        new("fai", "FAI Status", "FAI records with FAI type, inspection status, characteristic completion, result and Quality and Engineering approval.", true, false),
        new("repair", "Repair Status", "Repair records with category, technician, status, result, test and inspection status and disposition.", true, false),
        new("work-instructions", "Work Instruction Status", "Work instructions with type, revision, status, owner, reviewer, approver, effective date and last update.", true, false),
        new("user-activity", "User Activity", "Who did what in SEQUFLOH and when, with role, record reference and summary.", true, true),
        new("time-variance", "Time Variance", "Finished tasks with benchmark ranges, elapsed time, variance and the recorded reason.", true, false),
        new("production", "Production Tracking", "Production tasks.", false, false),
        new("parts", "Part Master", "Parts in the Part Master.", false, false),
        new("parts-lists", "Parts Lists", "Parts lists with their items.", false, false),
        new("task-log", "Task Log", "Every task event (dispatch, start, pause, resume, hold, completion and so on).", false, false),
        new("audit", "SEQUFLOH Audit Trail", "SEQUFLOH audit events.", false, true),
        new("approval-status", "Approval Status", "Approval status of work instructions and tracking records.", false, false),
    ];

    static ReportColumn C(string key, string label, string type = "text") => new(key, label, type);
    static string Words(object? s) => s is null ? "" : Regex.Replace(s.ToString()!, "([a-z])([A-Z])", "$1 $2");
    static double? Minutes(long seconds) => Math.Round(seconds / 60.0, 1);
    static readonly Dictionary<TrackingType, string> TypeLabel = new() { [TrackingType.Validation] = "Validation", [TrackingType.Fai] = "FAI", [TrackingType.Repair] = "Repair", [TrackingType.Production] = "Production" };
    static readonly Dictionary<WorkInstructionType, string> WiLabel = new() { [WorkInstructionType.Validation] = "Validation", [WorkInstructionType.ValidationFai] = "Validation / FAI", [WorkInstructionType.Fai] = "FAI",
        [WorkInstructionType.RepairTest] = "Repair Test", [WorkInstructionType.RepairWork] = "Repair Work Instruction" };

    public async Task<IReadOnlyList<SequflohReportInfo>> AvailableAsync(CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohViewReports, ct)) return [];
        var audit = await m.CanAnyAsync(Permissions.SequflohViewAuditTrail, ct);
        return Catalog.Where(c => !c.NeedsAudit || audit).ToList();
    }

    public async Task<bool> CanExportAsync(CancellationToken ct) =>
        (await f.GetSettingsAsync(ct)).Security.ExportsRequirePermission ? await m.CanAnyAsync(Permissions.SequflohExportRecords, ct) : await m.CanAnyAsync(Permissions.SequflohViewReports, ct);

    async Task<(DateTimeOffset? From, DateTimeOffset? To)> RangeAsync(SequflohReportFilter p, CancellationToken ct)
    {
        if (p.From is null && p.To is null) return (null, null);
        if (p.From > p.To) throw new AppException("The start date must be on or before the end date.");
        var zone = await m.ZoneAsync(p.SiteId, ct);
        DateTimeOffset U(DateOnly d) { var l = d.ToDateTime(TimeOnly.MinValue); return new DateTimeOffset(l, zone.GetUtcOffset(l)).ToUniversalTime(); }
        return (p.From is { } a ? U(a) : null, p.To is { } b ? U(b.AddDays(1)) : null);
    }

    async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
    static string? N(Dictionary<Guid, string> n, Guid? id) => id is { } g && n.TryGetValue(g, out var s) ? s : null;

    async Task<List<SequflohTrackingRecord>> TrackingAsync(SequflohReportFilter p, TrackingType? type, int limit, CancellationToken ct)
    {
        var q = await tracking.QueryAsync(ct);
        if (type is { } t) q = q.Where(x => x.Type == t);
        else if (Enum.TryParse<TrackingType>(p.Type, true, out var pt)) q = q.Where(x => x.Type == pt);
        if (p.SiteId is { } s) q = q.Where(x => x.SiteId == s);
        if (Enum.TryParse<TrackingStatus>(p.Status, true, out var st)) q = q.Where(x => x.Status == st);
        if (p.UserId is { } u) q = q.Where(x => x.AssignedUserId == u || x.OwnerId == u || x.CompletedById == u);
        if (!string.IsNullOrWhiteSpace(p.Q))
        {
            var like = $"%{p.Q.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.Number + " " + x.WorkInstructionNumber + " " + (x.PartNumber ?? "") + " " + (x.WorkOrder ?? "") + " " + (x.Operation ?? "") + " " + (x.SerialNumber ?? ""), like));
        }
        var (from, to) = await RangeAsync(p, ct);
        if (from is { } fr) q = q.Where(x => x.CreatedAt >= fr);
        if (to is { } tt) q = q.Where(x => x.CreatedAt < tt);
        return await q.OrderByDescending(x => x.CreatedAt).Take(limit).ToListAsync(ct);
    }

    IQueryable<AuditEntry> SequflohAudit() =>
        db.AuditEntries.AsNoTracking().Where(a => a.EntityType.StartsWith("Sequfloh") || (a.Changes != null && EF.Functions.JsonContains(a.Changes, "{\"module\":\"sequfloh\"}")));

    /// <summary>SEQUFLOH audit entries the caller may see: entries at sites in the caller's audit-trail scope, plus module-wide entries.</summary>
    public async Task<IQueryable<AuditEntry>> AuditQueryAsync(CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(m.UserId, Permissions.SequflohViewAuditTrail, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        var q = SequflohAudit();
        if (!scope.AllSites)
        {
            var sites = scope.SiteIds.Concat(scope.DepartmentPairs.Select(d => d.Site)).Distinct().ToList();
            q = q.Where(a => a.SiteId == null || sites.Contains(a.SiteId.Value));
        }
        return q;
    }

    public static IQueryable<AuditEntry> AuditView(IQueryable<AuditEntry> q, string view) => view switch
    {
        "system" => q,
        "instructions" => q.Where(a => a.EntityType == SequflohInstructionService.EntityType),
        "access" => q.Where(a => a.Action == "ModuleAccessed" || a.Action == "ModuleAccessChanged" || a.Action == "AccessDenied"),
        "changes" => q.Where(a => a.Action == "Created" || a.Action == "Updated" || a.Action == "Deleted" || a.Action == "Archived" || a.Action == "Restored"),
        "exports" => q.Where(a => a.Action == "Exported" || a.Action.EndsWith("Printed")),
        _ => throw new NotFoundException("Audit view"),
    };

    public async Task<IQueryable<AuditEntry>> FilterAuditAsync(IQueryable<AuditEntry> q, SequflohReportFilter p, CancellationToken ct)
    {
        if (p.UserId is { } u) q = q.Where(a => a.UserId == u);
        if (!string.IsNullOrWhiteSpace(p.Action)) q = q.Where(a => a.Action == p.Action);
        if (!string.IsNullOrWhiteSpace(p.Type)) q = q.Where(a => a.EntityType == p.Type);
        if (p.SiteId is { } s) q = q.Where(a => a.SiteId == s);
        if (!string.IsNullOrWhiteSpace(p.Q)) { var like = $"%{p.Q.Trim()}%"; q = q.Where(a => EF.Functions.ILike(a.Description + " " + a.UserName + " " + a.Action, like)); }
        var (from, to) = await RangeAsync(p, ct);
        if (from is { } fr) q = q.Where(a => a.OccurredAt >= fr);
        if (to is { } tt) q = q.Where(a => a.OccurredAt < tt);
        return q;
    }

    public async Task<ReportResult> RunAsync(string key, SequflohReportFilter p, int limit, CancellationToken ct)
    {
        var info = (await AvailableAsync(ct)).FirstOrDefault(c => c.Key == key) ?? throw new NotFoundException("Report");
        var settings = await f.GetSettingsAsync(ct);
        var title = settings.Export.ReportTitles.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t) ? t : info.Title;
        var (cols, rows) = await BuildAsync(key, p, limit, ct);
        if (settings.Export.IncludedColumns.TryGetValue(key, out var keep) && keep.Count > 0) cols = cols.Where(c => keep.Contains(c.Key)).ToList();
        return new ReportResult(new ReportDefinition(key, title, "SEQUFLOH", Permissions.SequflohViewReports, info.Description, true), new ReportParams(p.From, p.To, p.SiteId, null), cols, rows, null);
    }

    async Task<(List<ReportColumn>, List<Dictionary<string, object?>>)> BuildAsync(string key, SequflohReportFilter p, int limit, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        switch (key)
        {
            case "task-performance" or "production" or "validation" or "fai" or "repair" or "time-variance":
            {
                TrackingType? type = key switch { "production" => TrackingType.Production, "validation" => TrackingType.Validation, "fai" => TrackingType.Fai, "repair" => TrackingType.Repair, _ => null };
                var recs = await TrackingAsync(p, type, limit, ct);
                if (key == "time-variance") recs = recs.Where(r => r.ActualCompletion != null && (r.MinBenchmarkMinutes != null || r.MaxBenchmarkMinutes != null)).ToList();
                var names = await NamesAsync(recs.SelectMany(r => new[] { r.AssignedUserId, r.OwnerId, r.CompletedById }), ct);
                var wiIds = recs.Select(r => r.WorkInstructionId).Distinct().ToList();
                var wis = await db.SequflohWorkInstructions.AsNoTracking().Where(w => wiIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new { w.Type, w.RepairCategory }, ct);
                string Review(bool req, DateTimeOffset? at) => !req ? "Not required" : at is null ? "Pending" : "Reviewed";
                foreach (var r in recs)
                {
                    var d = new Dictionary<string, object?>
                    {
                        ["number"] = r.Number, ["type"] = TypeLabel[r.Type], ["status"] = Words(r.Status), ["result"] = Words(r.Result), ["instruction"] = $"{r.WorkInstructionNumber} rev {r.WorkInstructionRevision}",
                        ["part"] = r.PartNumber, ["operation"] = r.Operation, ["assigned"] = N(names, r.AssignedUserId), ["owner"] = N(names, r.OwnerId), ["plannedStart"] = r.PlannedStart, ["dueAt"] = r.DueAt,
                        ["actualStart"] = r.ActualStart, ["actualCompletion"] = r.ActualCompletion, ["minBenchmark"] = r.MinBenchmarkMinutes, ["maxBenchmark"] = r.MaxBenchmarkMinutes,
                        ["elapsedMinutes"] = Minutes(r.ActiveSeconds), ["varianceMinutes"] = r.VarianceMinutes, ["varianceReason"] = r.VarianceReason, ["approvalStatus"] = Words(r.ApprovalStatus),
                        ["reviewStatus"] = Review(r.QualityReviewRequired, r.QualityReviewedAt), ["deviations"] = r.Deviations, ["workOrder"] = r.WorkOrder, ["priority"] = r.Priority,
                        ["technician"] = N(names, r.CompletedById) ?? N(names, r.AssignedUserId), ["disposition"] = r.FinalDisposition, ["faiType"] = r.FaiType,
                        ["qualityApproval"] = Review(r.QualityReviewRequired, r.QualityReviewedAt), ["engineeringApproval"] = Review(r.EngineeringReviewRequired, r.EngineeringReviewedAt),
                        ["inspectionStatus"] = r.Status == TrackingStatus.PendingInspection ? "Pending inspection" : r.QualityReviewedAt is not null ? "Inspected" : r.QualityReviewRequired ? "Not inspected" : "Not required",
                        ["category"] = wis.TryGetValue(r.WorkInstructionId, out var w) ? w.RepairCategory : null,
                        ["testStatus"] = wis.TryGetValue(r.WorkInstructionId, out var w2) && w2.Type == WorkInstructionType.RepairTest ? Words(r.Result) : "No test",
                        ["_url"] = $"/sequfloh/record/{r.Id}",
                    };
                    if (r.Type == TrackingType.Fai)
                    {
                        var chars = JsonNode.Parse(r.Characteristics)?.AsArray() ?? [];
                        var done = chars.Count(c => !string.IsNullOrWhiteSpace(c?["result"]?.ToString()));
                        d["characteristics"] = chars.Count == 0 ? "None" : $"{done} of {chars.Count}";
                    }
                    rows.Add(d);
                }
                return (key switch
                {
                    "task-performance" => [C("number", "Task"), C("type", "Type"), C("status", "Status"), C("assigned", "Assigned"), C("part", "Part"), C("operation", "Operation"), C("plannedStart", "Planned start", "datetime"),
                        C("dueAt", "Due", "datetime"), C("actualStart", "Actual start", "datetime"), C("actualCompletion", "Actual completion", "datetime"), C("minBenchmark", "Minimum (min)", "number"),
                        C("maxBenchmark", "Maximum (min)", "number"), C("elapsedMinutes", "Elapsed (min)", "number"), C("varianceMinutes", "Variance (min)", "number"), C("result", "Result")],
                    "validation" => [C("number", "Record"), C("instruction", "Instruction / revision"), C("part", "Part"), C("owner", "Owner"), C("plannedStart", "Planned", "datetime"), C("dueAt", "Due", "datetime"),
                        C("status", "Status"), C("result", "Result"), C("reviewStatus", "Review status"), C("approvalStatus", "Approval status"), C("deviations", "Deviations")],
                    "fai" => [C("number", "FAI number"), C("instruction", "Instruction / revision"), C("part", "Part"), C("faiType", "FAI type"), C("inspectionStatus", "Inspection status"),
                        C("characteristics", "Characteristics complete"), C("result", "Result"), C("qualityApproval", "Quality approval"), C("engineeringApproval", "Engineering approval")],
                    "repair" => [C("number", "Repair number"), C("instruction", "Instruction / revision"), C("part", "Part"), C("category", "Category"), C("technician", "Technician"), C("status", "Status"),
                        C("result", "Result"), C("testStatus", "Test status"), C("inspectionStatus", "Inspection status"), C("disposition", "Disposition")],
                    "time-variance" => [C("number", "Task"), C("type", "Type"), C("part", "Part"), C("operation", "Operation"), C("minBenchmark", "Minimum (min)", "number"), C("maxBenchmark", "Maximum (min)", "number"),
                        C("elapsedMinutes", "Elapsed (min)", "number"), C("varianceMinutes", "Variance (min)", "number"), C("varianceReason", "Reason"), C("result", "Result")],
                    _ => [C("number", "Task"), C("instruction", "Instruction / revision"), C("part", "Part"), C("workOrder", "Work order"), C("operation", "Operation"), C("assigned", "Assigned"), C("status", "Status"),
                        C("priority", "Priority"), C("dueAt", "Due", "datetime"), C("actualCompletion", "Completed", "datetime"), C("elapsedMinutes", "Elapsed (min)", "number"), C("result", "Result")],
                }, rows);
            }
            case "work-instructions" or "approval-status":
            {
                var q = await instructions.QueryAsync(ct);
                if (p.SiteId is { } s) q = q.Where(x => x.SiteId == s);
                if (Enum.TryParse<WorkInstructionStatus>(p.Status, true, out var st)) q = q.Where(x => x.Status == st);
                if (Enum.TryParse<WorkInstructionType>(p.Type, true, out var ty)) q = q.Where(x => x.Type == ty);
                if (p.UserId is { } u) q = q.Where(x => x.OwnerId == u || x.AuthorId == u || x.ReviewerId == u || x.ApproverId == u);
                if (!string.IsNullOrWhiteSpace(p.Q)) { var like = $"%{p.Q.Trim()}%"; q = q.Where(x => EF.Functions.ILike(x.Number + " " + x.Title + " " + (x.PartNumber ?? ""), like)); }
                var (from, to) = await RangeAsync(p, ct);
                if (from is { } fr) q = q.Where(x => x.UpdatedAt >= fr);
                if (to is { } tt) q = q.Where(x => x.UpdatedAt < tt);
                var list = await q.OrderBy(x => x.Number).ThenBy(x => x.Revision).Take(limit).ToListAsync(ct);
                var names = await NamesAsync(list.SelectMany(x => new[] { x.OwnerId, x.ReviewerId, x.ApproverId, x.ReviewedById, x.ApprovedById }), ct);
                foreach (var x in list)
                    rows.Add(new()
                    {
                        ["number"] = x.Number, ["type"] = WiLabel[x.Type], ["title"] = x.Title, ["revision"] = x.Revision, ["status"] = Words(x.Status), ["owner"] = N(names, x.OwnerId),
                        ["reviewer"] = N(names, x.ReviewedById) ?? N(names, x.ReviewerId), ["approver"] = N(names, x.ApprovedById) ?? N(names, x.ApproverId), ["effectiveDate"] = x.EffectiveDate,
                        ["updatedAt"] = x.UpdatedAt, ["reviewedAt"] = x.ReviewedAt, ["approvedAt"] = x.ApprovedAt, ["releasedAt"] = x.ReleasedAt, ["kind"] = "Work instruction",
                        ["approval"] = x.ApprovedAt is not null ? "Approved" : x.Status == WorkInstructionStatus.PendingApproval ? "Pending approval" : x.Status == WorkInstructionStatus.PendingReview ? "Pending review" : "Not approved",
                        ["_url"] = $"/sequfloh/instruction/{x.Id}",
                    });
                if (key == "approval-status")
                {
                    var recs = await TrackingAsync(p with { Status = null, Type = null }, null, limit, ct);
                    foreach (var r in recs.Where(r => r.ApprovalStatus != "NotRequired"))
                        rows.Add(new() { ["kind"] = "Tracking record", ["number"] = r.Number, ["type"] = TypeLabel[r.Type], ["title"] = r.WorkInstructionTitle, ["revision"] = r.WorkInstructionRevision,
                            ["status"] = Words(r.Status), ["approval"] = Words(r.ApprovalStatus), ["updatedAt"] = r.UpdatedAt, ["_url"] = $"/sequfloh/record/{r.Id}" });
                    return ([C("kind", "Item"), C("number", "Number"), C("type", "Type"), C("title", "Title"), C("revision", "Revision"), C("status", "Status"), C("approval", "Approval status"),
                        C("reviewer", "Reviewed by"), C("reviewedAt", "Reviewed", "datetime"), C("approver", "Approved by"), C("approvedAt", "Approved", "datetime"), C("updatedAt", "Last update", "datetime")], rows);
                }
                return ([C("number", "Instruction"), C("type", "Type"), C("title", "Title"), C("revision", "Revision"), C("status", "Status"), C("owner", "Owner"), C("reviewer", "Reviewer"), C("approver", "Approver"),
                    C("effectiveDate", "Effective date", "date"), C("updatedAt", "Last update", "datetime")], rows);
            }
            case "user-activity" or "audit":
            {
                var q = await FilterAuditAsync(await AuditQueryAsync(ct), p, ct);
                var list = await q.OrderByDescending(a => a.OccurredAt).Take(limit).ToListAsync(ct);
                var userIds = list.Where(a => a.UserId != null).Select(a => a.UserId!.Value).Distinct().ToList();
                var roles = (await db.UserRoleAssignments.AsNoTracking().Where(r => userIds.Contains(r.UserId)).Join(db.QmsRoles, r => r.RoleId, x => x.Id, (r, x) => new { r.UserId, x.Name }).ToListAsync(ct))
                    .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name).Distinct().Order()));
                foreach (var a in list)
                    rows.Add(new()
                    {
                        ["occurredAt"] = a.OccurredAt, ["user"] = a.UserName, ["role"] = a.UserId is { } uid && roles.TryGetValue(uid, out var rn) ? rn : null, ["module"] = "SEQUFLOH", ["action"] = Words(a.Action),
                        ["record"] = Words(a.EntityType.Replace("Sequfloh", "")), ["summary"] = a.Description, ["reason"] = a.Reason, ["_id"] = a.Id,
                    });
                return (key == "audit"
                    ? [C("occurredAt", "When", "datetime"), C("user", "Who"), C("action", "What"), C("record", "Record"), C("summary", "Description"), C("reason", "Reason")]
                    : [C("user", "User"), C("role", "Role"), C("module", "Module"), C("action", "Action"), C("occurredAt", "Timestamp", "datetime"), C("record", "Record reference"), C("summary", "Summary")], rows);
            }
            case "task-log":
            {
                var recs = await tracking.QueryAsync(ct);
                if (p.SiteId is { } s) recs = recs.Where(x => x.SiteId == s);
                var q = db.SequflohTaskEvents.AsNoTracking().Where(e => recs.Any(r => r.Id == e.TrackingRecordId));
                if (!string.IsNullOrWhiteSpace(p.Action)) q = q.Where(e => e.EventType == p.Action);
                if (p.UserId is { } u) q = q.Where(e => e.UserId == u);
                if (!string.IsNullOrWhiteSpace(p.Q)) { var like = $"%{p.Q.Trim()}%"; q = q.Where(e => EF.Functions.ILike(e.TrackingNumber + " " + e.UserName + " " + (e.Reason ?? "") + " " + (e.Comment ?? ""), like)); }
                var (from, to) = await RangeAsync(p, ct);
                if (from is { } fr) q = q.Where(e => e.CreatedAt >= fr);
                if (to is { } tt) q = q.Where(e => e.CreatedAt < tt);
                foreach (var e in await q.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync(ct))
                    rows.Add(new() { ["at"] = e.CreatedAt, ["number"] = e.TrackingNumber, ["event"] = Words(e.EventType), ["from"] = Words(e.FromStatus), ["to"] = Words(e.ToStatus), ["user"] = e.UserName,
                        ["reason"] = e.Reason ?? e.Comment, ["activeMinutes"] = Minutes(e.ActiveSeconds), ["pausedMinutes"] = Minutes(e.PausedSeconds), ["holdMinutes"] = Minutes(e.HoldSeconds),
                        ["_url"] = $"/sequfloh/record/{e.TrackingRecordId}?tab=events" });
                return ([C("at", "When", "datetime"), C("number", "Task"), C("event", "Event"), C("from", "From"), C("to", "To"), C("user", "User"), C("reason", "Reason / comment"),
                    C("activeMinutes", "Active (min)", "number"), C("pausedMinutes", "Paused (min)", "number"), C("holdMinutes", "On hold (min)", "number")], rows);
            }
            case "parts":
            {
                var q = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct);
                if (p.SiteId is { } s) q = q.Where(x => x.SiteId == s);
                if (!string.IsNullOrWhiteSpace(p.Q)) { var like = $"%{p.Q.Trim()}%"; q = q.Where(x => EF.Functions.ILike(x.PartNumber + " " + x.Name + " " + (x.Description ?? ""), like)); }
                foreach (var x in await q.OrderBy(x => x.PartNumber).Take(limit).ToListAsync(ct))
                    rows.Add(new() { ["partNumber"] = x.PartNumber, ["name"] = x.Name, ["revision"] = x.Revision, ["type"] = Words(x.PartType), ["family"] = x.ProductFamily, ["uom"] = x.UnitOfMeasure,
                        ["repairable"] = x.IsRepairable, ["serialized"] = x.IsSerialized, ["status"] = Words(x.Status), ["_url"] = $"/sequfloh/parts/{x.Id}" });
                return ([C("partNumber", "Part number"), C("name", "Name"), C("revision", "Revision"), C("type", "Part type"), C("family", "Product family"), C("uom", "Unit"), C("repairable", "Repairable"),
                    C("serialized", "Serialized"), C("status", "Status")], rows);
            }
            case "parts-lists":
            {
                IQueryable<SequflohPartsList> q = db.SequflohPartsLists.AsNoTracking().Include(x => x.Items);
                if (!string.IsNullOrWhiteSpace(p.Q)) { var like = $"%{p.Q.Trim()}%"; q = q.Where(x => EF.Functions.ILike(x.Number + " " + x.Title, like)); }
                if (Enum.TryParse<PartsListStatus>(p.Status, true, out var st)) q = q.Where(x => x.Status == st);
                var lists = await q.OrderBy(x => x.Number).Take(limit).ToListAsync(ct);
                var partIds = lists.Where(l => l.ParentPartId != null).Select(l => l.ParentPartId!.Value).Distinct().ToList();
                var parts = await db.Parts.AsNoTracking().Where(x => partIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.PartNumber, ct);
                foreach (var l in lists)
                    foreach (var i in l.Items.OrderBy(i => i.ItemNumber).DefaultIfEmpty())
                        rows.Add(new() { ["number"] = l.Number, ["title"] = l.Title, ["revision"] = l.Revision, ["listType"] = Words(l.ListType), ["status"] = Words(l.Status),
                            ["parent"] = l.ParentPartId is { } pp && parts.TryGetValue(pp, out var pn) ? pn : null, ["item"] = i?.ItemNumber, ["part"] = i?.PartNumber, ["description"] = i?.Description,
                            ["quantity"] = i?.Quantity, ["uom"] = i?.Unit, ["_url"] = $"/sequfloh/parts-lists/{l.Id}" });
                return ([C("number", "Parts list"), C("title", "Title"), C("revision", "Revision"), C("listType", "List type"), C("status", "Status"), C("parent", "Parent part"), C("item", "Item", "number"),
                    C("part", "Part number"), C("description", "Description"), C("quantity", "Quantity", "number"), C("uom", "Unit")], rows);
            }
            default: throw new NotFoundException("Report");
        }
    }

    /// <summary>Counts by status for the Task Performance summary.</summary>
    public static Dictionary<string, int> StatusCounts(ReportResult r) =>
        r.Rows.GroupBy(x => x.GetValueOrDefault("status")?.ToString() ?? "").ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Workbook for a report: title block (report, organization, generated on and by, filters), then the full filtered table. Audited.</summary>
    public async Task<(byte[] Content, string FileName)> ExportAsync(string key, SequflohReportFilter p, IReadOnlyList<(string, string)> filterLabels, string? fileName, CancellationToken ct)
    {
        if (!await CanExportAsync(ct)) throw new ForbiddenException();
        var r = await RunAsync(key, p, ExportLimit, ct);
        var settings = await f.GetSettingsAsync(ct);
        var org = string.IsNullOrWhiteSpace(settings.Export.OrganizationHeading)
            ? await db.OrganizationSettings.AsNoTracking().Select(s => s.OrganizationName).FirstOrDefaultAsync(ct) ?? "SEQUFLOH" : settings.Export.OrganizationHeading;
        var zone = await m.ZoneAsync(p.SiteId, ct);
        var now = TimeZoneInfo.ConvertTime(m.Now, zone);
        var meta = new List<(string, string)> { ("Report", r.Definition.Title), ("Organization", org), ("Module", "SEQUFLOH"), ("Generated", now.ToString(settings.Export.DateFormat) + " " + zone.Id),
            ("Generated by", current.UserName), ("Filters", filterLabels.Count == 0 ? "None" : string.Join("; ", filterLabels.Select(x => $"{x.Item1}: {x.Item2}"))), ("Rows", r.Rows.Count.ToString()) };
        var bytes = ReportService.ToXlsx(r, r.Definition.Title, meta);
        var name = string.IsNullOrWhiteSpace(fileName) ? settings.Export.FilenamePattern.Replace("{report}", key).Replace("{date}", now.ToString("yyyyMMdd-HHmm")) : fileName.Trim();
        name = Regex.Replace(name, @"[^A-Za-z0-9._ -]", "-");
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
        f.Audit("Exported", "SequflohReport", Guid.Empty, $"Exported {r.Definition.Title} ({r.Rows.Count} rows) to Excel as {name}", p.SiteId, context: new { report = key, rows = r.Rows.Count, filters = filterLabels.Select(x => $"{x.Item1}: {x.Item2}") });
        await db.SaveChangesAsync(ct);
        return (bytes, name);
    }
}
