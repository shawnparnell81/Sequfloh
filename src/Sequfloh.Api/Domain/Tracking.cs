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
