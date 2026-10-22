namespace Sequfloh.Domain.Audit;

/// <summary>Append-only business audit trail entry: who, what, when, description.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Description { get; set; } = "";
    public string? Changes { get; set; }
    public Guid? SiteId { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
}
