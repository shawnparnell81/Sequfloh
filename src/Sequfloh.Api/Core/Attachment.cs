using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Shared;

/// <summary>A file attached to a record (pictures used in instruction blocks, evidence files). The bytes live in file storage.</summary>
public class Attachment : Entity, IAudited, IArchivable
{
    public string OwnerType { get; set; } = "";
    public Guid OwnerId { get; set; }
    public Guid? SiteId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsLocked { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string AuditLabel => $"file {FileName}";
}
