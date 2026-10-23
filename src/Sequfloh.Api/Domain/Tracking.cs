using System.Text.Json.Serialization;
using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackingType { Validation = 0, Fai = 1, Repair = 2, Production = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackingStatus { NotStarted = 0, Assigned = 1, InProgress = 2, Paused = 3, OnHold = 4, Completed = 7, Failed = 8, Canceled = 10 }

/// <summary>
/// Operational tracking record and executable task (validation, FAI, repair, production). It stores the exact released work instruction revision it was
/// created from, with steps, characteristics and parts copied at creation, so later revisions never change history. Timing is persisted: the time spent in
/// each state is accumulated from stored timestamps at every transition, and a state version guards every change.
/// </summary>
public class SequflohTrackingRecord : Entity, IAudited, ISiteScoped, ISoftDeletable
{
    /// <summary>Informational status from the admin-editable status list, for example "Quarantine hold (NetSuite)". Never blocks anything; official holds are in NetSuite.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string? StatusTag { get; set; }
    /// <summary>Optional free-text note that goes with the status.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(300)]
    public string? StatusNote { get; set; }
    public string Number { get; set; } = "";
    public TrackingType Type { get; set; }
    public Guid WorkInstructionId { get; set; }
    public string WorkInstructionNumber { get; set; } = "";
    public string WorkInstructionRevision { get; set; } = "";
    public string WorkInstructionTitle { get; set; } = "";
    public Guid? PartId { get; set; }
    public string? PartNumber { get; set; }
    public string? PartDescription { get; set; }
    public string? SerialNumber { get; set; }
    /// <summary>How many units this job should do, for example 2300 assemblies; null when not counted.</summary>
    public int? QuantityTarget { get; set; }
    /// <summary>Quantity repaired on the job so far, kept for the whole job (not per person).</summary>
    public int QuantityCompleted { get; set; }
    /// <summary>Units that could not be repaired (a plain count; scrap and disposition are handled in the ERP).</summary>
    public int QuantityNotRepaired { get; set; }
    /// <summary>Parts replaced on each assembly (JSON array of partNumber, description, perAssembly and an optional quantity override).</summary>
    public string PartsReplaced { get; set; } = "[]";
    /// <summary>How the columns of this job's tester result files are read (JSON), remembered once set.</summary>
    public string TestColumns { get; set; } = "{}";
    /// <summary>Report sections to include (JSON object of section name to true/false). Empty means the summary only.</summary>
    public string ReportOptions { get; set; } = "{}";
    /// <summary>Findings / nonconformance for a supplier chargeback (JSON object; every field optional).</summary>
    public string Findings { get; set; } = "{}";
    /// <summary>Other chargeback cost lines (JSON array of description and amount).</summary>
    public string CostLines { get; set; } = "[]";
    public string? LotNumber { get; set; }
    public string? WorkOrder { get; set; }
    public string? RepairOrder { get; set; }
    public string? CustomerReturnReference { get; set; }
    public string? Project { get; set; }
    public string? Operation { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? AssignedUserId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid SiteId { get; set; }
    public TrackingStatus Status { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateTimeOffset? PlannedStart { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualCompletion { get; set; }
    public Guid? CompletedById { get; set; }

    // persisted timing
    public DateTimeOffset? CurrentStateStartedAt { get; set; }
    public long ActiveSeconds { get; set; }
    public long PausedSeconds { get; set; }
    public long HoldSeconds { get; set; }
    public Guid? ActiveOperatorId { get; set; }
    public int StateVersion { get; set; }
    public Guid? LastTransitionId { get; set; }
    public DateTimeOffset? LastTransitionAt { get; set; }
    public decimal? MinBenchmarkMinutes { get; set; }
    public decimal? MaxBenchmarkMinutes { get; set; }
    /// <summary>Minutes outside the benchmark range at completion (negative below the minimum, positive above the maximum, 0 inside).</summary>
    public decimal? VarianceMinutes { get; set; }
    public string? VarianceReason { get; set; }

    public string Result { get; set; } = "Pending";
    public string? EngineeringConclusion { get; set; }
    public string? EvidenceReferences { get; set; }
    public string? TechnicianComments { get; set; }
    public string? QualityComments { get; set; }
    public string? EngineeringComments { get; set; }
    public string? Deviations { get; set; }
    public string? NonconformanceReferences { get; set; }
    public string? PauseReason { get; set; }
    public string? HoldReason { get; set; }
    public string? CancelReason { get; set; }
    public string? FailureReason { get; set; }
    public string? ReportedCondition { get; set; }
    public string? ObservedCondition { get; set; }
    public string? RepairDescription { get; set; }
    public string? FinalDisposition { get; set; }
    public string? FaiType { get; set; }
    /// <summary>Samples, controlled conditions, FAI verifications, post-repair inspection and repair-test results (key -> value).</summary>
    public string Details { get; set; } = "{}";
    /// <summary>Steps copied from the instruction revision, each with its result, evidence, acknowledgement and verification.</summary>
    public string Steps { get; set; } = "[]";
    public string Characteristics { get; set; } = "[]";
    public string PartsRows { get; set; } = "[]";
    public string AuditLabel => $"{Type.ToString().ToLowerInvariant()} record {Number}";
}

/// <summary>Append-only task event (separate from the audit trail). The operation ID is unique, so the same transition cannot be stored twice.</summary>
public class SequflohTaskEvent : Entity, IAppendOnly
{
    public Guid TrackingRecordId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public string EventType { get; set; } = "";
    public TrackingStatus? FromStatus { get; set; }
    public TrackingStatus? ToStatus { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public Guid? AssignedBeforeId { get; set; }
    public Guid? AssignedAfterId { get; set; }
    public string? Reason { get; set; }
    public string? Comment { get; set; }
    public long ActiveSeconds { get; set; }
    public long PausedSeconds { get; set; }
    public long HoldSeconds { get; set; }
    public Guid OperationId { get; set; }
}

/// <summary>A person assigned to a work order. A work order can have several; the first one assigned is also kept as the lead on the record.</summary>
public class SequflohTaskAssignee
{
    public Guid TrackingRecordId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// Live time one person logged on a work order (start to stop), used for chargebacks. Anyone signed in can log time on any work order, assigned or not,
/// and several people can be clocked on the same work order at once.
/// </summary>
public class SequflohTimeEntry : Entity, IAudited
{
    public Guid TrackingRecordId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    /// <summary>Worked seconds, paused time excluded.</summary>
    public long Seconds { get; set; }
    public long PausedSeconds { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    /// <summary>Start, pause, resume and stop with their times (JSON array of kind and at).</summary>
    public string Events { get; set; } = "[]";
    public string AuditLabel => $"time on {TrackingNumber} for {UserName}";
}


/// <summary>A tester result file attached to a job, optionally for a batch or test date. CSV and XLSX files are read into pass and fail counts.</summary>
public class SequflohTestFile : Entity, IAudited
{
    public Guid TrackingRecordId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Batch { get; set; }
    public DateOnly? TestDate { get; set; }
    public string UploadedByName { get; set; } = "";
    public int? Tested { get; set; }
    public int? Passed { get; set; }
    public int? Failed { get; set; }
    /// <summary>Key values read from the file (JSON array of column, min, average, max).</summary>
    public string KeyValues { get; set; } = "[]";
    /// <summary>test, attachment or evidence (photos).</summary>
    public string Kind { get; set; } = "test";
    public string AuditLabel => $"test file {FileName} on {TrackingNumber}";
}
