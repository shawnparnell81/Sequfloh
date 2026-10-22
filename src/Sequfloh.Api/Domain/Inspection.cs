using System.Text.Json.Serialization;
using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectionRequestStatus { Open = 0, InProgress = 1, Done = 2, Cancelled = 3 }

/// <summary>A request from anyone to have parts inspected. It can be turned into a work order, which carries its fields over.</summary>
public class SequflohInspectionRequest : Entity, IAudited, ISiteScoped
{
    public string Number { get; set; } = "";
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? PartId { get; set; }
    public string PartNumber { get; set; } = "";
    public string? PartDescription { get; set; }
    public decimal? Quantity { get; set; }
    public string? Reason { get; set; }
    public string? Requirement { get; set; }
    public Guid? WorkInstructionId { get; set; }
    public string? WorkInstructionLabel { get; set; }
    public DateOnly? RequestedDate { get; set; }
    public string Priority { get; set; } = "Normal";
    public InspectionRequestStatus Status { get; set; }
    public Guid RequestedById { get; set; }
    public string RequestedByName { get; set; } = "";
    public Guid? WorkOrderId { get; set; }
    public string? WorkOrderNumber { get; set; }
    public string AuditLabel => $"inspection request {Number}";
}
