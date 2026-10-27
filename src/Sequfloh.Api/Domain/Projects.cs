using System.Text.Json.Serialization;
using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Execution;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectStatus { Open = 0, Closed = 1 }

/// <summary>A project groups work orders of any type and inspection requests so their hours and costs can be totalled together. The number is typed by the user.</summary>
public class SequflohProject : Entity, IAudited
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? SiteId { get; set; }
    public ProjectStatus Status { get; set; }
    /// <summary>"Include 8D Report?" Yes: the record shows the 8D Report block.</summary>
    public bool Include8D { get; set; }
    public string AuditLabel => $"project {Number}";
}
