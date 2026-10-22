using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record TrackingCreateInput(TrackingType Type, Guid WorkInstructionId, Guid SiteId, Guid? DepartmentId = null, Guid? PartId = null, string? SerialNumber = null, string? LotNumber = null,
    string? WorkOrder = null, string? RepairOrder = null, string? CustomerReturnReference = null, string? Project = null, string? Operation = null, Guid? AssignedUserId = null, string? Priority = null,
    DateTimeOffset? PlannedStart = null, DateTimeOffset? DueAt = null, decimal? MinBenchmarkMinutes = null, decimal? MaxBenchmarkMinutes = null,
    string? ReportedCondition = null);
public sealed record TrackingSaveInput(Guid OperationId, int StateVersion, string? SerialNumber = null, string? LotNumber = null, string? WorkOrder = null, string? RepairOrder = null,
    string? CustomerReturnReference = null, string? Project = null, string? Priority = null, DateTimeOffset? PlannedStart = null, DateTimeOffset? DueAt = null, string? EvidenceReferences = null,
    string? TechnicianComments = null, string? QualityComments = null, string? EngineeringComments = null, string? Deviations = null, string? NonconformanceReferences = null,
    string? ReportedCondition = null, string? ObservedCondition = null, string? FinalDisposition = null, string? VarianceReason = null, string? Result = null, string? EngineeringConclusion = null,
    JsonObject? Details = null, JsonArray? Steps = null, JsonArray? Characteristics = null, JsonArray? PartsRows = null);
public sealed record TaskActionInput(Guid OperationId, int StateVersion, string? Reason = null, string? Comment = null, Guid? AssignedUserId = null, Guid? ExpectedAssignedUserId = null,
    TrackingStatus? TargetStatus = null, string? VarianceReason = null);

/// <summary>
/// SEQUFLOH operational tracking (validation, FAI, repair, production). A record is created from a released work instruction revision and keeps a copy of
/// its steps, characteristics and parts, so later revisions never change history. Every action locks the row, checks the stored state version and the
/// legal transition, accumulates persisted timing from database timestamps, appends a task event with a unique operation ID and writes an audit entry.
/// Repeating an operation ID returns the stored result without applying the change twice.
/// </summary>
public sealed class SequflohTrackingService(AppDbContext db, ModuleSupport m, INumberingService numbers, SequflohFoundationService f,
    SequflohNotifications notify)
{
    public const string EntityType = nameof(SequflohTrackingRecord);
    public static readonly TrackingStatus[] Terminal = [TrackingStatus.Completed, TrackingStatus.Failed, TrackingStatus.Canceled];
    public static readonly TrackingStatus[] Open = [TrackingStatus.NotStarted, TrackingStatus.Assigned, TrackingStatus.InProgress, TrackingStatus.Paused, TrackingStatus.OnHold];
    static readonly TrackingStatus[] Working = [TrackingStatus.InProgress];
    static string? T(string? s, int max) { if (string.IsNullOrWhiteSpace(s)) return null; s = s.Trim(); return s.Length > max ? throw new AppException($"A value is limited to {max} characters.") : s; }

    public static string CreatePermission(TrackingType t) => t switch
    {
        TrackingType.Validation => Permissions.SequflohCreateValidationRecord, TrackingType.Fai => Permissions.SequflohCreateFaiRecord,
        TrackingType.Repair => Permissions.SequflohCreateRepairRecord, _ => Permissions.SequflohCreateProductionTask
    };
    static string NumberingType(TrackingType t) => t switch
    {
        TrackingType.Validation => "SqfValidationRecord", TrackingType.Fai => "SqfFaiRecord", TrackingType.Repair => "SqfRepairRecord", _ => "SqfProductionTask"
    };
    /// <summary>Instruction types a tracking record of this type may execute.</summary>
    public static WorkInstructionType[] InstructionTypes(TrackingType t) => t switch
    {
        TrackingType.Validation => [WorkInstructionType.Validation, WorkInstructionType.ValidationFai],
        TrackingType.Fai => [WorkInstructionType.Fai, WorkInstructionType.ValidationFai],
        TrackingType.Repair => [WorkInstructionType.RepairWork],
        _ => [WorkInstructionType.Validation, WorkInstructionType.ValidationFai, WorkInstructionType.Fai, WorkInstructionType.RepairTest, WorkInstructionType.RepairWork]
    };

    /// <summary>Task transitions: allowed source states, target (null keeps the state), permission, reason rule, event type.</summary>
    public sealed record TaskRule(string Action, string Label, TrackingStatus[] From, TrackingStatus? To, string Permission, bool ReasonRequired, string EventType);
    public static readonly IReadOnlyList<TaskRule> Rules =
    [
        new("dispatch", "Dispatch", [TrackingStatus.NotStarted], TrackingStatus.Assigned, Permissions.SequflohDispatchTask, false, "Dispatch"),
        new("assign", "Assign", [TrackingStatus.NotStarted, TrackingStatus.Assigned], TrackingStatus.Assigned, Permissions.SequflohDispatchTask, false, "Assignment"),
        new("start", "Start", [TrackingStatus.NotStarted, TrackingStatus.Assigned], TrackingStatus.InProgress, Permissions.SequflohStartTask, false, "Start"),
        new("pause", "Pause", [TrackingStatus.InProgress], TrackingStatus.Paused, Permissions.SequflohPauseTask, true, "Pause"),
        new("resume", "Resume", [TrackingStatus.Paused, TrackingStatus.OnHold], TrackingStatus.InProgress, Permissions.SequflohResumeTask, false, "Resume"),
        new("hold", "Place on hold", [TrackingStatus.NotStarted, TrackingStatus.Assigned, TrackingStatus.InProgress, TrackingStatus.Paused], TrackingStatus.OnHold, Permissions.SequflohHoldTask, true, "Hold"),
        new("restart", "Restart", [TrackingStatus.Failed], TrackingStatus.Assigned, Permissions.SequflohDispatchTask, true, "Restart"),
        new("complete", "Complete", [TrackingStatus.InProgress], TrackingStatus.Completed, Permissions.SequflohCompleteTask, false, "Completion"),
        new("fail", "Fail", [TrackingStatus.InProgress], TrackingStatus.Failed, Permissions.SequflohFailTask, true, "Failure"),
        new("reassign", "Reassign", [TrackingStatus.NotStarted, TrackingStatus.Assigned, TrackingStatus.InProgress, TrackingStatus.Paused, TrackingStatus.OnHold], null, Permissions.SequflohReassignTask, false, "Reassignment"),
        new("cancel", "Cancel", [TrackingStatus.NotStarted, TrackingStatus.Assigned, TrackingStatus.InProgress, TrackingStatus.Paused, TrackingStatus.OnHold], TrackingStatus.Canceled, Permissions.SequflohCancelTask, true, "Cancellation"),
        new("override", "Administrative override", [.. Enum.GetValues<TrackingStatus>()], null, Permissions.SequflohManageSettings, true, "AdministrativeOverride"),
    ];

    // ------------------------------------------------------------------ queries
    public async Task<IQueryable<SequflohTrackingRecord>> QueryAsync(CancellationToken ct) => await m.ScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct);

    public async Task<SequflohTrackingRecord> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var q = track ? db.SequflohTrackingRecords : db.SequflohTrackingRecords.AsNoTracking();
        var r = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Tracking record");
        await m.RequireAsync(Permissions.SequflohAccess, r.SiteId, r.DepartmentId, ct);
        return r;
    }

    string me = "";
    async Task<SequflohTrackingRecord> LockAsync(Guid id, CancellationToken ct)
    {
        me = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM sqf.sequfloh_tracking_records WHERE \"Id\" = {id} FOR UPDATE", ct);
        var r = await LoadAsync(id, true, ct);
        await db.Entry(r).ReloadAsync(ct); // latest committed state after the lock
        return r;
    }

    /// <summary>Database clock (the record of truth for timing, never the browser).</summary>
    public async Task<DateTimeOffset> DbNowAsync(CancellationToken ct) => await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    /// <summary>Active, paused and hold seconds at <paramref name="now"/>: stored totals plus the running time of the current state.</summary>
    public static (long Active, long Paused, long Hold) Durations(SequflohTrackingRecord r, DateTimeOffset now)
    {
        var run = r.CurrentStateStartedAt is { } s ? Math.Max(0, (long)(now - s).TotalSeconds) : 0;
        return (r.ActiveSeconds + (r.Status == TrackingStatus.InProgress ? run : 0), r.PausedSeconds + (r.Status == TrackingStatus.Paused ? run : 0),
            r.HoldSeconds + (r.Status == TrackingStatus.OnHold ? run : 0));
    }

    /// <summary>Actions the current user can take now (state and permission).</summary>
    public async Task<List<object>> AvailableActionsAsync(SequflohTrackingRecord r, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var a in Rules.Where(a => a.From.Contains(r.Status) && a.Action != "override"))
            if (await m.CanAsync(a.Permission, r.SiteId, r.DepartmentId, ct)) list.Add(new { a.Action, a.Label, a.ReasonRequired });
        return list;
    }

    /// <summary>The next required action shown on the Active Station, derived from the stored state and the completion rules.</summary>
    public static string NextAction(SequflohTrackingRecord r, IReadOnlyList<string> problems) => r.Status switch
    {
        TrackingStatus.NotStarted => "Dispatch the task to a user.",
        TrackingStatus.Assigned => "Start the task.",
        TrackingStatus.Paused => $"Resume the task (paused: {r.PauseReason ?? "no reason"}).",
        TrackingStatus.OnHold => $"Resolve the hold and resume (hold: {r.HoldReason ?? "no reason"}).",
        TrackingStatus.InProgress when problems.Count > 0 => problems[0],
        TrackingStatus.InProgress => "Complete the task.",
        TrackingStatus.Failed => "Restart for rework when authorized.",
        _ => "No action required."
    };

    // ------------------------------------------------------------------ create
    public async Task<SequflohTrackingRecord> CreateAsync(TrackingCreateInput r, CancellationToken ct)
    {
        await m.RequireAsync(CreatePermission(r.Type), r.SiteId, r.DepartmentId, ct);
        await m.ValidateLocationAsync(r.SiteId, r.DepartmentId, ct);
        var wi = await db.SequflohWorkInstructions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.WorkInstructionId, ct) ?? throw new AppException("Choose a released work instruction.");
        if (wi.Status != WorkInstructionStatus.Released) throw new AppException($"{wi.Number} rev {wi.Revision} is not released. Only a released revision can be executed.");
        if (!InstructionTypes(r.Type).Contains(wi.Type)) throw new AppException($"{wi.Number} is not a work instruction that a {Label(r.Type)} record can use.");
        if (r.AssignedUserId is { } au)
        {
            await m.RequireActiveUserAsync(au, "assigned user", ct);
            if (!await m.UserCanAsync(au, Permissions.SequflohStartTask, r.SiteId, r.DepartmentId, ct)) throw new AppException("The assigned user cannot start SEQUFLOH tasks at this site.");
        }
        var settings = await f.GetSettingsAsync(ct);
        var priority = T(r.Priority, 100) ?? "Normal";
        await f.RequireOptionAsync("priorities", priority, "Priority", ct);
        if (r.MinBenchmarkMinutes < 0 || r.MaxBenchmarkMinutes < 0) throw new AppException("Benchmarks cannot be negative.");
        if (r.MinBenchmarkMinutes is { } mn && r.MaxBenchmarkMinutes is { } mx && mn > mx) throw new AppException("The minimum benchmark cannot be greater than the maximum benchmark.");
        if (r.PlannedStart is { } ps && r.DueAt is { } du && du < ps) throw new AppException("The due date cannot be before the planned start.");
        var partId = r.PartId ?? wi.PartId;
        if (r.Type == TrackingType.Repair && partId is null) throw new AppException("Choose the repairable part.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        me = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var now = await DbNowAsync(ct);
        var rec = new SequflohTrackingRecord
        {
            Type = r.Type, WorkInstructionId = wi.Id, WorkInstructionNumber = wi.Number, WorkInstructionRevision = wi.Revision, WorkInstructionTitle = wi.Title,
            SiteId = r.SiteId, DepartmentId = r.DepartmentId ?? wi.DepartmentId, OwnerId = m.UserId, AssignedUserId = r.AssignedUserId, Priority = priority,
            SerialNumber = T(r.SerialNumber, 100), LotNumber = T(r.LotNumber, 100), WorkOrder = T(r.WorkOrder, 100), RepairOrder = T(r.RepairOrder, 100),
            CustomerReturnReference = T(r.CustomerReturnReference, 100), Project = T(r.Project, 100), Operation = T(r.Operation, 300) ?? wi.Operation, PlannedStart = r.PlannedStart, DueAt = r.DueAt,
            Status = r.AssignedUserId is null ? TrackingStatus.NotStarted : TrackingStatus.Assigned, CurrentStateStartedAt = now, LastTransitionAt = now,
            ReportedCondition = T(r.ReportedCondition, 4000), RepairDescription = wi.RepairDescription, FaiType = wi.FaiType,
            MinBenchmarkMinutes = r.MinBenchmarkMinutes ?? (r.Type == TrackingType.Production ? settings.Tracking.DefaultMinimumMinutes : null),
            MaxBenchmarkMinutes = r.MaxBenchmarkMinutes ?? (r.Type == TrackingType.Production ? settings.Tracking.DefaultMaximumMinutes : null),
        };
        if (partId is { } pid)
        {
            var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new AppException("The part does not exist in the Part Master.");
            if (p.Status != Domain.MasterData.MasterStatus.Active) throw new AppException($"Part {p.PartNumber} is not active.");
            if (r.Type == TrackingType.Repair && !p.IsRepairable) throw new AppException($"Part {p.PartNumber} is not marked repairable in the Part Master.");
            if (p.IsSerialized && rec.SerialNumber is null && r.Type == TrackingType.Repair) throw new AppException($"Part {p.PartNumber} is serialized. Enter the serial number.");
            rec.PartId = p.Id; rec.PartNumber = p.PartNumber; rec.PartDescription = p.Name;
        }
        // the instruction revision's content is copied (execution fields start empty)
        rec.Steps = Snapshot(wi.Steps); rec.Characteristics = Snapshot(wi.Characteristics); rec.PartsRows = Snapshot(wi.PartsRows);
        var content = JsonNode.Parse(wi.Content)!.AsObject();
        var details = new JsonObject();
        if (content["samples"] is JsonObject smp) details["samplePlan"] = smp.DeepClone();
        if (content["conditions"] is JsonObject cnd) details["conditionLimits"] = cnd.DeepClone();
        rec.Details = details.ToJsonString();
        rec.Number = await numbers.NextAsync(NumberingType(r.Type), null, ct);
        db.SequflohTrackingRecords.Add(rec);
        if (r.Type == TrackingType.Validation && rec.PartId is { } attachFor) await Sequfloh.Drawings.DrawingService.AttachToRecordAsync(db, rec.Id, attachFor, ct);
        Event(rec, rec.AssignedUserId is null ? "Created" : "Dispatch", null, rec.Status, null, rec.AssignedUserId, null, $"Created from {wi.Number} rev {wi.Revision}", Guid.NewGuid());
        f.Audit("TrackingRecordCreated", EntityType, rec.Id, $"{rec.Number} ({Label(r.Type)}) created from {wi.Number} rev {wi.Revision}", rec.SiteId,
            context: new { trackingType = r.Type.ToString(), workInstruction = wi.Number, revision = wi.Revision, part = rec.PartNumber });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(rec, ct);
        await notify.TrackingChangedAsync(rec, "create", null, ct);
        return rec;
    }

    static string Snapshot(string json)
    {
        var a = JsonNode.Parse(json)!.AsArray();
        foreach (var r in a) if (r is JsonObject o) { o["instructionRowId"] = o["id"]?.DeepClone(); o["id"] = Guid.NewGuid().ToString(); o.Remove("actualResult"); o.Remove("result"); }
        return a.ToJsonString();
    }

    // ------------------------------------------------------------------ save (data entry)
    static readonly string[] StepEntry = ["result", "actualResult", "evidence", "comment", "completed"];
    static readonly string[] CharEntry = ["actualResult", "result", "evidenceReference", "nonconformanceReference", "notes"];
    static readonly string[] PartsEntry = ["issuedQuantity", "usedQuantity", "returnedQuantity", "scrapQuantity", "lotNumber", "serialNumber"];
    static readonly string[] DetailKeys = ["sampleIdentification", "controlledConditions", "materialVerification", "specialProcessVerification", "certificateReview", "drawingBalloonReferences",
        "dimensionalResults", "visualResults", "functionalResults", "repairTestResult", "repairTestReference", "postRepairInspection", "failureHandling", "rejectionHandling", "resultEntry", "qualityCheckpoints"];

    public async Task<SequflohTrackingRecord> SaveAsync(Guid id, TrackingSaveInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await LockAsync(id, ct);
        if (await db.SequflohTaskEvents.AnyAsync(e => e.OperationId == r.OperationId, ct)) return rec; // repeated submission
        if (r.StateVersion != rec.StateVersion) throw new ConflictException("The tracking record changed since you opened it. Reload and try again.");
        if (rec.Status is TrackingStatus.Completed or TrackingStatus.Canceled) throw new ConflictException($"{rec.Number} is {Words(rec.Status)} and cannot be changed.");
        var changed = new List<string>();
        var settings = await f.GetSettingsAsync(ct);
        bool Diff(string? a, string? b) => (a ?? "") != (b ?? "");
        // header (planning) fields: dispatcher or creator of this record type
        var serial = T(r.SerialNumber, 100); var lot = T(r.LotNumber, 100); var wo = T(r.WorkOrder, 100); var ro = T(r.RepairOrder, 100); var cr = T(r.CustomerReturnReference, 100);
        var project = T(r.Project, 100); var priority = T(r.Priority, 100) ?? rec.Priority;
        if (Diff(serial, rec.SerialNumber) || Diff(lot, rec.LotNumber) || Diff(wo, rec.WorkOrder) || Diff(ro, rec.RepairOrder) || Diff(cr, rec.CustomerReturnReference) || Diff(project, rec.Project)
            || Diff(priority, rec.Priority) || r.PlannedStart != rec.PlannedStart || r.DueAt != rec.DueAt)
        {
            if (!await m.CanAsync(CreatePermission(rec.Type), rec.SiteId, rec.DepartmentId, ct) && !await m.CanAsync(Permissions.SequflohDispatchTask, rec.SiteId, rec.DepartmentId, ct))
                throw new ForbiddenException();
            if (Terminal.Contains(rec.Status)) throw new ConflictException("Planning details cannot change once the record is closed.");
            await f.RequireOptionAsync("priorities", priority, "Priority", ct);
            if (r.PlannedStart is { } ps && r.DueAt is { } du && du < ps) throw new AppException("The due date cannot be before the planned start.");
            rec.SerialNumber = serial; rec.LotNumber = lot; rec.WorkOrder = wo; rec.RepairOrder = ro; rec.CustomerReturnReference = cr; rec.Project = project; rec.Priority = priority;
            rec.PlannedStart = r.PlannedStart; rec.DueAt = r.DueAt;
            changed.Add("planning details");
        }
        // execution entry: the person working the record while it is in progress
        var tech = (Ev: T(r.EvidenceReferences, 4000), Tc: T(r.TechnicianComments, 4000), Dv: T(r.Deviations, 4000), Nc: T(r.NonconformanceReferences, 4000), Rc: T(r.ReportedCondition, 4000),
            Oc: T(r.ObservedCondition, 4000), Fd: T(r.FinalDisposition, 100), Vr: T(r.VarianceReason, 4000), Res: T(r.Result, 100));
        var steps = r.Steps is null ? rec.Steps : MergeRows(rec.Steps, r.Steps, StepEntry, "step");
        var chars = r.Characteristics is null ? rec.Characteristics : MergeRows(rec.Characteristics, r.Characteristics, CharEntry, "characteristic");
        var partsRows = r.PartsRows is null ? rec.PartsRows : MergeRows(rec.PartsRows, r.PartsRows, PartsEntry, "parts row");
        var details = r.Details is null ? rec.Details : MergeDetails(rec.Details, r.Details);
        var entryChanged = Diff(tech.Ev, rec.EvidenceReferences) || Diff(tech.Tc, rec.TechnicianComments) || Diff(tech.Dv, rec.Deviations) || Diff(tech.Nc, rec.NonconformanceReferences)
            || Diff(tech.Rc, rec.ReportedCondition) || Diff(tech.Oc, rec.ObservedCondition) || Diff(tech.Fd, rec.FinalDisposition) || Diff(tech.Vr, rec.VarianceReason)
            || (rec.Type == TrackingType.Production && tech.Res is not null && tech.Res != rec.Result)
            || !JsonNode.DeepEquals(JsonNode.Parse(steps), JsonNode.Parse(rec.Steps)) || !JsonNode.DeepEquals(JsonNode.Parse(chars), JsonNode.Parse(rec.Characteristics))
            || !JsonNode.DeepEquals(JsonNode.Parse(partsRows), JsonNode.Parse(rec.PartsRows)) || !JsonNode.DeepEquals(JsonNode.Parse(details), JsonNode.Parse(rec.Details));
        if (entryChanged)
        {
            await m.RequireAsync(Permissions.SequflohStartTask, rec.SiteId, rec.DepartmentId, ct);
            if (!Working.Contains(rec.Status)) throw new ConflictException($"Results can be recorded only while the record is in progress. {rec.Number} is {Words(rec.Status)}.");
            if (tech.Fd is not null)
                await f.RequireOptionAsync(rec.Type == TrackingType.Fai ? "inspection-results" : "repair-dispositions", tech.Fd, "Final disposition", ct);
            CheckStepResults(steps);
            ValidateQuantities(partsRows);
            if (rec.Type == TrackingType.Production && tech.Res is not null && tech.Res is not ("Pending" or "Pass" or "Fail" or "Conditional")) throw new AppException("The result must be Pending, Pass, Fail or Conditional.");
            var detailNode = JsonNode.Parse(details)!.AsObject();
            if (detailNode["repairTestResult"]?.ToString() is { Length: > 0 } rtr) await f.RequireOptionAsync("repair-test-results", rtr, "Repair test result", ct);
            if (Diff(tech.Ev, rec.EvidenceReferences)) changed.Add("evidence references");
            if (Diff(tech.Tc, rec.TechnicianComments) || Diff(tech.Dv, rec.Deviations) || Diff(tech.Oc, rec.ObservedCondition) || Diff(tech.Rc, rec.ReportedCondition)) changed.Add("comments");
            if (Diff(tech.Vr, rec.VarianceReason)) changed.Add("variance reason");
            changed.Add("results");
            rec.EvidenceReferences = tech.Ev; rec.TechnicianComments = tech.Tc; rec.Deviations = tech.Dv; rec.NonconformanceReferences = tech.Nc; rec.ReportedCondition = tech.Rc;
            rec.ObservedCondition = tech.Oc; rec.FinalDisposition = tech.Fd; rec.VarianceReason = tech.Vr;
            rec.Steps = steps; rec.Characteristics = chars; rec.PartsRows = partsRows; rec.Details = details;
            var before = rec.Result;
            rec.Result = rec.Type == TrackingType.Production ? (tech.Res ?? rec.Result) : ComputeResult(rec);
            if (before != rec.Result) changed.Add($"result {before} -> {rec.Result}");
        }
        // review comments
        var qc = T(r.QualityComments, 4000);
        if (Diff(qc, rec.QualityComments))
        {
            await m.RequireAsync(Permissions.SequflohEditQualityFolder, rec.SiteId, rec.DepartmentId, ct);
            rec.QualityComments = qc; changed.Add("Quality comments");
        }
        var ec = T(r.EngineeringComments, 4000); var concl = T(r.EngineeringConclusion, 4000);
        if (Diff(ec, rec.EngineeringComments) || Diff(concl, rec.EngineeringConclusion))
        {
            await m.RequireAsync(Permissions.SequflohEditEngineeringFolder, rec.SiteId, rec.DepartmentId, ct);
            rec.EngineeringComments = ec; rec.EngineeringConclusion = concl; changed.Add("Engineering comments");
        }
        if (changed.Count == 0) return rec;
        var now = await DbNowAsync(ct);
        rec.StateVersion++; rec.LastTransitionId = r.OperationId; rec.LastTransitionAt = now;
        Event(rec, "RecordUpdate", rec.Status, rec.Status, rec.AssignedUserId, rec.AssignedUserId, null, string.Join("; ", changed), r.OperationId, now);
        f.Audit("TrackingRecordUpdated", EntityType, rec.Id, $"{rec.Number}: {string.Join(", ", changed)} recorded", rec.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(rec, ct);
        return rec;
    }

    /// <summary>Merges execution fields into the stored copy by row id; instruction content in the copy cannot be changed and rows cannot be added or removed.</summary>
    static string MergeRows(string stored, JsonArray incoming, string[] entry, string what)
    {
        var rows = JsonNode.Parse(stored)!.AsArray();
        foreach (var n in incoming)
        {
            if (n is not JsonObject src || src["id"]?.ToString() is not { } rid) throw new AppException($"Each {what} needs its id.");
            var dst = rows.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.ToString() == rid) ?? throw new AppException($"The {what} does not belong to this record.");
            foreach (var k in entry)
            {
                if (!src.ContainsKey(k)) continue;
                var v = src[k];
                if (v is null || (v is JsonValue sv && sv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))) { dst.Remove(k); continue; }
                if (v is not JsonValue jv) throw new AppException($"The {what} {Words(k)} has an invalid value.");
                if (k.EndsWith("Quantity", StringComparison.Ordinal))
                {
                    var d = jv.TryGetValue<decimal>(out var dd) ? dd : jv.TryGetValue<string>(out var ds) && decimal.TryParse(ds, NumberStyles.Number, CultureInfo.InvariantCulture, out var dp) ? dp
                        : throw new AppException($"The {what} {Words(k)} must be a number.");
                    dst[k] = d;
                }
                else if (k is "completed") dst[k] = jv.TryGetValue<bool>(out var b) ? b : throw new AppException($"The {what} {Words(k)} must be yes or no.");
                else { var t = jv.ToString().Trim(); if (t.Length > 2000) throw new AppException($"The {what} {Words(k)} is limited to 2000 characters."); dst[k] = t; }
            }
        }
        return rows.ToJsonString();
    }

    static string MergeDetails(string stored, JsonObject incoming)
    {
        var o = JsonNode.Parse(stored)!.AsObject();
        foreach (var k in DetailKeys)
        {
            if (!incoming.ContainsKey(k)) continue;
            var v = incoming[k]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(v)) o.Remove(k);
            else o[k] = v.Length > 4000 ? throw new AppException($"{Words(k)} is limited to 4000 characters.") : v;
        }
        return o.ToJsonString();
    }

    static void CheckStepResults(string json)
    {
        foreach (var s in JsonNode.Parse(json)!.AsArray().OfType<JsonObject>())
            if (s["result"]?.ToString() is { } res && res is not ("Pass" or "Fail" or "Conditional" or "Not applicable" or "Complete"))
                throw new AppException($"Step {s["sequence"]}: the result must be Pass, Fail, Conditional, Not applicable or Complete.");
    }

    static decimal? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    static void ValidateQuantities(string json)
    {
        foreach (var p in JsonNode.Parse(json)!.AsArray().OfType<JsonObject>())
        {
            var pn = p["replacementPartNumber"]?.ToString() ?? "parts row";
            decimal? iss = Num(p["issuedQuantity"]), used = Num(p["usedQuantity"]), ret = Num(p["returnedQuantity"]), scr = Num(p["scrapQuantity"]);
            if (new[] { iss, used, ret, scr }.Any(x => x < 0)) throw new AppException($"{pn}: quantities cannot be negative.");
            if (iss is { } i && (used ?? 0) + (ret ?? 0) + (scr ?? 0) > i) throw new AppException($"{pn}: used, returned and scrap quantities cannot exceed the issued quantity ({i.ToString(CultureInfo.InvariantCulture)}).");
            if (iss is null && (used ?? 0) > 0) throw new AppException($"{pn}: record the issued quantity before the used quantity.");
        }
    }

    /// <summary>Overall result from the recorded results: any fail fails, any conditional is conditional, everything recorded and passing passes, otherwise pending.</summary>
    public static string ComputeResult(SequflohTrackingRecord rec)
    {
        var results = new List<string?>();
        if (rec.Type is TrackingType.Validation or TrackingType.Repair)
            results.AddRange(JsonNode.Parse(rec.Steps)!.AsArray().Select(s => s?["result"]?.ToString()));
        if (rec.Type == TrackingType.Fai)
            results.AddRange(JsonNode.Parse(rec.Characteristics)!.AsArray().Select(c => c?["result"]?.ToString()));
        if (rec.Type == TrackingType.Repair && JsonNode.Parse(rec.Details)!["repairTestResult"]?.ToString() is { Length: > 0 } rt) results.Add(rt == "Pass" ? "Pass" : rt == "Fail" ? "Fail" : null);
        if (results.Count == 0) return "Pending";
        if (results.Any(r => r == "Fail")) return "Fail";
        if (results.Any(r => r is null)) return "Pending";
        if (results.Any(r => r == "Conditional")) return "Conditional";
        return "Pass";
    }

    /// <summary>Problems that block completion or submission: required steps, results, evidence, comments, quantities, tests, disposition.</summary>
    public async Task<List<string>> CompletionProblemsAsync(SequflohTrackingRecord rec, CancellationToken ct)
    {
        var p = new List<string>();
        var settings = await f.GetSettingsAsync(ct);
        var steps = JsonNode.Parse(rec.Steps)!.AsArray().OfType<JsonObject>().ToList();
        string Seq(JsonObject s) => s["sequence"]?.ToString() ?? "?";
        bool Has(JsonObject s, string k) => !string.IsNullOrWhiteSpace(s[k]?.ToString());
        bool Flag(JsonObject s, string k) => s[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        switch (rec.Type)
        {
            case TrackingType.Validation:
                foreach (var s in steps)
                {
                    if (!Has(s, "result")) { p.Add($"Step {Seq(s)}: record the result."); continue; }
                    if (Flag(s, "actualResultRequired") && !Has(s, "actualResult")) p.Add($"Step {Seq(s)}: record the actual result.");
                    if (settings.Validation.EvidenceRequired && Has(s, "evidenceRequired") && !Has(s, "evidence")) p.Add($"Step {Seq(s)}: record the evidence reference.");
                    if (s["result"]?.ToString() is "Fail" or "Conditional" && !Has(s, "comment")) p.Add($"Step {Seq(s)}: a {s["result"]!.ToString().ToLowerInvariant()} result needs a comment.");
                }
                if (rec.Result == "Conditional" && string.IsNullOrWhiteSpace(rec.Deviations)) p.Add("A conditional result needs the deviation recorded.");
                if (rec.Result == "Fail" && string.IsNullOrWhiteSpace(rec.NonconformanceReferences) && string.IsNullOrWhiteSpace(rec.TechnicianComments)) p.Add("A failed validation needs a nonconformance reference or a technician comment.");
                break;
            case TrackingType.Fai:
                foreach (var c in JsonNode.Parse(rec.Characteristics)!.AsArray().OfType<JsonObject>())
                {
                    var n = c["characteristicNumber"]?.ToString() ?? Seq(c);
                    if (!Has(c, "result")) { p.Add($"Characteristic {n}: record the result."); continue; }
                    if (c["result"]!.ToString() != "Not applicable" && !Has(c, "actualResult")) p.Add($"Characteristic {n}: record the actual result.");
                    if (c["result"]!.ToString() == "Fail" && settings.Fai.EvidenceRequiredForFailures && !Has(c, "evidenceReference")) p.Add($"Characteristic {n}: a failed characteristic needs an evidence reference.");
                    if (c["result"]!.ToString() == "Pass" && decimal.TryParse(c["actualResult"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
                        && ((Num(c["lowerLimit"]) is { } lo && a < lo) || (Num(c["upperLimit"]) is { } hi && a > hi)))
                        p.Add($"Characteristic {n}: the actual result {a.ToString(CultureInfo.InvariantCulture)} is outside the limits and cannot pass.");
                }
                if (string.IsNullOrWhiteSpace(rec.FinalDisposition)) p.Add("Choose the final disposition.");
                break;
            case TrackingType.Repair:
                foreach (var s in steps)
                {
                    if (!Flag(s, "completed") && !Has(s, "result")) { p.Add($"Operation {Seq(s)}: mandatory operation is incomplete."); continue; }
                    if (Flag(s, "passOrFailRequired") && s["result"]?.ToString() is not ("Pass" or "Fail")) p.Add($"Operation {Seq(s)}: record pass or fail.");
                    if (Flag(s, "technicianResultRequired") && !Has(s, "actualResult")) p.Add($"Operation {Seq(s)}: record the technician result.");
                }
                foreach (var pr in JsonNode.Parse(rec.PartsRows)!.AsArray().OfType<JsonObject>())
                {
                    var pn = pr["replacementPartNumber"]?.ToString() ?? "Parts row";
                    decimal? iss = Num(pr["issuedQuantity"]), used = Num(pr["usedQuantity"]), ret = Num(pr["returnedQuantity"]), scr = Num(pr["scrapQuantity"]);
                    if (Flag(pr, "required") && (used ?? 0) <= 0) p.Add($"{pn}: record the used quantity of this required part.");
                    if (iss is { } i && (used ?? 0) + (ret ?? 0) + (scr ?? 0) != i) p.Add($"{pn}: used, returned and scrap quantities must account for the issued quantity.");
                    if (Flag(pr, "lotNumberRequired") && (used ?? 0) > 0 && !Has(pr, "lotNumber")) p.Add($"{pn}: record the lot number.");
                    if (Flag(pr, "serialNumberRequired") && (used ?? 0) > 0 && !Has(pr, "serialNumber")) p.Add($"{pn}: record the serial number.");
                }
                var d = JsonNode.Parse(rec.Details)!.AsObject();
                if (settings.Repair.RepairTestRequired && string.IsNullOrWhiteSpace(d["repairTestResult"]?.ToString())) p.Add("Record the repair test result.");
                if (string.IsNullOrWhiteSpace(rec.ObservedCondition)) p.Add("Record the observed condition.");
                if (string.IsNullOrWhiteSpace(rec.FinalDisposition)) p.Add("Choose the final disposition.");
                break;
            default:
                if (rec.Result is "Pending" or "") p.Add("Record the result.");
                break;
        }
        return p;
    }

    // ------------------------------------------------------------------ transitions
    public async Task<SequflohTrackingRecord> ActAsync(Guid id, string action, TaskActionInput r, CancellationToken ct)
    {
        var rule = Rules.FirstOrDefault(a => a.Action == action) ?? throw new NotFoundException("Action");
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rec = await LockAsync(id, ct); // latest database state, locked until commit
        if (await db.SequflohTaskEvents.AnyAsync(e => e.OperationId == r.OperationId, ct)) return rec; // the same operation was already applied
        await m.RequireAsync(rule.Permission, rec.SiteId, rec.DepartmentId, ct);
        if (r.StateVersion != rec.StateVersion) throw new ConflictException("The task changed since you opened it. Reload and try again.");
        if (!rule.From.Contains(rec.Status)) throw new ConflictException($"{rule.Label} is not possible while {rec.Number} is {Words(rec.Status)}.");
        var reason = T(r.Reason, 300); var comment = T(r.Comment, 4000);
        if (rule.ReasonRequired && reason is null && !(action == "override" && !settings.Security.RequireReasonForOverride)) throw new AppException($"Enter a reason to {rule.Label.ToLowerInvariant()}.");
        var from = rec.Status;
        var to = rule.To ?? rec.Status;
        var before = rec.AssignedUserId;
        switch (action)
        {
            case "hold": rec.HoldReason = reason; break;
            case "pause": rec.PauseReason = reason; break;
            case "resume": rec.PauseReason = null; rec.HoldReason = null; break;
            case "dispatch" or "assign" or "reassign":
                var target = r.AssignedUserId ?? (action == "dispatch" ? rec.AssignedUserId : null) ?? throw new AppException("Choose the user to assign.");
                if (action == "reassign" && r.ExpectedAssignedUserId != rec.AssignedUserId) throw new ConflictException("The task was reassigned by someone else. Reload and try again.");
                if (action == "reassign" && target == rec.AssignedUserId) throw new AppException("The task is already assigned to that user.");
                await m.RequireActiveUserAsync(target, "assigned user", ct);
                if (!await m.UserCanAsync(target, Permissions.SequflohStartTask, rec.SiteId, rec.DepartmentId, ct)) throw new AppException("The selected user cannot start SEQUFLOH tasks at this site.");
                rec.AssignedUserId = target;
                if (action == "reassign" && rec.ActiveOperatorId is not null) rec.ActiveOperatorId = target;
                break;
            case "start":
                if (rec.AssignedUserId is { } a && a != m.UserId && !await m.CanAsync(Permissions.SequflohReassignTask, rec.SiteId, rec.DepartmentId, ct))
                    throw new ConflictException("The task is assigned to someone else.");
                rec.AssignedUserId ??= m.UserId;
                break;
            case "restart":
                rec.Result = rec.Type == TrackingType.Production ? "Pending" : ComputeResult(rec); rec.ActualCompletion = null; rec.CompletedById = null;
                rec.FailureReason = null;
                to = rec.AssignedUserId is null ? TrackingStatus.NotStarted : TrackingStatus.Assigned;
                break;
            case "complete":
                await RequireCompleteAsync(rec, ct);
                if (rec.Result == "Fail") throw new AppException("The result is Fail. Record the failure instead of completing.");
                break;
            case "fail": rec.FailureReason = reason; if (rec.Type == TrackingType.Production || rec.Result != "Fail") rec.Result = "Fail"; break;
            case "cancel": rec.CancelReason = reason; break;
            case "override":
                to = r.TargetStatus ?? throw new AppException("Choose the status to set.");
                if (to == from) throw new AppException("The task already has that status.");
                break;
        }
        var now = await DbNowAsync(ct);
        if (to != from)
        {
            Accumulate(rec, now);
            if (from == TrackingStatus.InProgress && to != TrackingStatus.Paused && to != TrackingStatus.OnHold)
            {
                rec.VarianceMinutes = Variance(rec);
                var vr = T(r.VarianceReason, 4000);
                if (vr is not null) rec.VarianceReason = vr;
                if (to is TrackingStatus.Completed && rec.VarianceMinutes is not null and not 0 && settings.Tracking.VarianceReasonRequired && string.IsNullOrWhiteSpace(rec.VarianceReason))
                    throw new AppException($"The time is outside the benchmark range by {Math.Abs(rec.VarianceMinutes.Value).ToString("0.##", CultureInfo.InvariantCulture)} minutes. Enter the variance reason.");
            }
            if (to == TrackingStatus.InProgress) { rec.ActiveOperatorId = rec.AssignedUserId ?? m.UserId; rec.ActualStart ??= now; }
            else if (from == TrackingStatus.InProgress) rec.ActiveOperatorId = to is TrackingStatus.Paused or TrackingStatus.OnHold ? rec.ActiveOperatorId : null;
            if (Terminal.Contains(to)) { rec.ActualCompletion = now; rec.CompletedById ??= m.UserId; rec.ActiveOperatorId = null; }
            if (to is TrackingStatus.Completed) rec.CompletedById = m.UserId;
        }
        rec.Status = to;
        rec.StateVersion++; rec.LastTransitionId = r.OperationId; rec.LastTransitionAt = now;
        Event(rec, rule.EventType, from, to, before, rec.AssignedUserId, reason, comment, r.OperationId, now);
        var who = before != rec.AssignedUserId ? $" (assigned {await NameAsync(before, ct)} -> {await NameAsync(rec.AssignedUserId, ct)})" : "";
        f.Audit("Task" + string.Concat(action.Split('-').Select(Cap)), EntityType, rec.Id, $"{rec.Number}: {rule.Label.ToLowerInvariant()}, {Words(from)} -> {Words(to)}{who}", rec.SiteId, reason ?? comment,
            new { operationId = r.OperationId, stateVersion = rec.StateVersion });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(rec, ct);
        await notify.TrackingChangedAsync(rec, action, before, ct);
        return rec;
    }

    // module search reads the records directly, so there is no separate index to keep up to date
    static Task IndexAsync(SequflohTrackingRecord _, CancellationToken __) => Task.CompletedTask;

    async Task RequireCompleteAsync(SequflohTrackingRecord rec, CancellationToken ct)
    {
        var p = await CompletionProblemsAsync(rec, ct);
        if (p.Count > 0) throw new AppException(string.Join(" ", p));
    }

    /// <summary>Adds the time spent in the current state to its total (from database timestamps) and starts timing the next state.</summary>
    static void Accumulate(SequflohTrackingRecord r, DateTimeOffset now)
    {
        if (r.CurrentStateStartedAt is { } s)
        {
            var sec = Math.Max(0, (long)(now - s).TotalSeconds);
            switch (r.Status)
            {
                case TrackingStatus.InProgress: r.ActiveSeconds += sec; break;
                case TrackingStatus.Paused: r.PausedSeconds += sec; break;
                case TrackingStatus.OnHold: r.HoldSeconds += sec; break;
            }
        }
        r.CurrentStateStartedAt = now;
    }

    /// <summary>Minutes outside the benchmark range (negative below the minimum, positive above the maximum, 0 inside); null without a benchmark.</summary>
    public static decimal? Variance(SequflohTrackingRecord r)
    {
        if (r.MinBenchmarkMinutes is null && r.MaxBenchmarkMinutes is null) return null;
        var mins = Math.Round(r.ActiveSeconds / 60m, 2);
        if (r.MinBenchmarkMinutes is { } mn && mins < mn) return mins - mn;
        if (r.MaxBenchmarkMinutes is { } mx && mins > mx) return mins - mx;
        return 0;
    }

    void Event(SequflohTrackingRecord rec, string type, TrackingStatus? from, TrackingStatus? to, Guid? before, Guid? after, string? reason, string? comment, Guid op, DateTimeOffset? now = null)
    {
        var (a, p, h) = now is { } n ? Durations(rec, n) : (rec.ActiveSeconds, rec.PausedSeconds, rec.HoldSeconds);
        db.SequflohTaskEvents.Add(new SequflohTaskEvent
        {
            TrackingRecordId = rec.Id, TrackingNumber = rec.Number, EventType = type, FromStatus = from, ToStatus = to, UserId = m.UserId,
            UserName = me, AssignedBeforeId = before, AssignedAfterId = after,
            Reason = reason, Comment = comment, ActiveSeconds = a, PausedSeconds = p, HoldSeconds = h, OperationId = op
        });
    }

    async Task<string> NameAsync(Guid? id, CancellationToken ct) => id is null ? "nobody" : await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "unknown";
    public static string Label(TrackingType t) => t switch { TrackingType.Fai => "FAI", TrackingType.Validation => "validation", TrackingType.Repair => "repair", _ => "production" };
    public static string Words(TrackingStatus s) => SequflohInstructionService.Words(s.ToString());
    static string Words(string s) => SequflohInstructionService.Words(s);
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
