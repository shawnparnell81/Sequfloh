using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

/// <summary>
/// One person's arrival and departure for a shop day. Separate from work-order time: it answers when they came in and when they left.
/// Staff are salaried, so this is not a payroll clock. An open row has no <see cref="EndedAt"/>.
/// </summary>
public class SequflohDayClock : Entity, IAudited
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>The department they belonged to when they clocked in, so a team view stays stable if the assignment changes later.</summary>
    public Guid? DepartmentId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Manual, Automatic, or HardCap. Empty while the day is still open.</summary>
    public string? EndKind { get; set; }
    /// <summary>The person chose to stay past the scheduled end of the day, until they clock out or the hard cap.</summary>
    public bool WorkingLate { get; set; }
    public string AuditLabel => $"day clock for {UserName}";
}

/// <summary>
/// One stretch of a day: a work order, an indirect task, a break, or lunch. Only one segment is open at a time.
/// Switching tasks ends the previous stretch. A break remembers what to resume.
/// </summary>
public class SequflohDaySegment : Entity, IAudited
{
    public Guid DayClockId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>WorkOrder, Indirect, Break, or Lunch.</summary>
    public string Kind { get; set; } = "";
    public Guid? TrackingRecordId { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>The work-order time entry this stretch belongs to, when <see cref="Kind"/> is WorkOrder.</summary>
    public Guid? TimeEntryId { get; set; }
    public Guid? IndirectValueId { get; set; }
    public string? TaskName { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>Switch, Pause, Stop, Resume, ClockOut, Automatic, or HardCap.</summary>
    public string? EndKind { get; set; }
    public long Seconds { get; set; }
    /// <summary>On a break or lunch: WorkOrder or Indirect, so Resume can continue that task.</summary>
    public string? ResumeKind { get; set; }
    public Guid? ResumeTrackingRecordId { get; set; }
    public string? ResumeTrackingNumber { get; set; }
    public Guid? ResumeTimeEntryId { get; set; }
    public Guid? ResumeIndirectValueId { get; set; }
    public string? ResumeTaskName { get; set; }
    public string? ResumeNote { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset? OriginalStartedAt { get; set; }
    public DateTimeOffset? OriginalEndedAt { get; set; }
    public DateTimeOffset? AdjustedAt { get; set; }
    public string? AdjustedBy { get; set; }
    public string? AdjustReason { get; set; }
    public string AuditLabel => $"{Kind.ToLowerInvariant()} time for {UserName}";
}
