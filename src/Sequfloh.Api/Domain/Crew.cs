using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

/// <summary>
/// Time for a crew of unnamed workers (for example day labor on a rebox or inspection job). One start, pause, continue and stop applies to the whole crew.
/// Labor hours are worked time (paused time left out) times the crew size, which is kept per session so it can change from day to day.
/// </summary>
public class SequflohCrewSession : Entity, IAudited
{
    public Guid TrackingRecordId { get; set; }
    public string TrackingNumber { get; set; } = "";
    public int CrewSize { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    /// <summary>Paused seconds of finished pauses.</summary>
    public long PausedSeconds { get; set; }
    /// <summary>Start, pause, continue and stop with their times (JSON array of kind and at).</summary>
    public string Events { get; set; } = "[]";
    /// <summary>True when the session was typed in after the fact rather than clocked live.</summary>
    public bool Entered { get; set; }
    public string? Note { get; set; }
    public string AuditLabel => $"crew time on {TrackingNumber}";
}

/// <summary>A saved supplier chargeback report. The work order number is typed by the user; inventory stays in NetSuite.</summary>
public class SequflohChargebackReport : Entity, IAudited
{
    public string WorkOrderNumber { get; set; } = "";
    public Guid? TrackingRecordId { get; set; }
    public string? TrackingNumber { get; set; }
    public Guid? SiteId { get; set; }
    public string? Supplier { get; set; }
    public string? SupplierReference { get; set; }
    public Guid? PartId { get; set; }
    public string? PartNumber { get; set; }
    public string? PartDescription { get; set; }
    public string? TaskDescription { get; set; }
    public string? Result { get; set; }
    public int? QtyInspected { get; set; }
    public int? QtyBad { get; set; }
    public int? TotalPersonnel { get; set; }
    public decimal? TotalHours { get; set; }
    public decimal? CrewHours { get; set; }
    public decimal? UserHours { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public decimal? HourlyRate { get; set; }
    public string? DiscoveryMethod { get; set; }
    public string? SupplierPartNumber { get; set; }
    public string? LotDateCodes { get; set; }
    public string? RootCause { get; set; }
    public string? CorrectiveAction { get; set; }
    /// <summary>Parts replaced (JSON array of partNumber, description, quantity, unitCost).</summary>
    public string Parts { get; set; } = "[]";
    /// <summary>Other cost lines (JSON array of description and amount).</summary>
    public string CostLines { get; set; } = "[]";
    public string? Notes { get; set; }
    public string CreatedByName { get; set; } = "";
    public string AuditLabel => $"chargeback report {WorkOrderNumber}";
}
