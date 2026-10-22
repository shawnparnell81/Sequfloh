using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Drawings;

/// <summary>A folder of drawings, for example one per part number. Folders can hold subfolders and can be linked to a part.</summary>
public class DrawingFolder : Entity, IAudited, IArchivable, ISiteScoped
{
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public Guid? PartId { get; set; }
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string AuditLabel => $"drawing folder {Name}";
}

/// <summary>A drawing in a folder. Every upload is a revision; earlier revisions stay downloadable.</summary>
public class Drawing : Entity, IAudited, IArchivable, ISiteScoped
{
    public Guid FolderId { get; set; }
    public string Title { get; set; } = "";
    public string? DrawingNumber { get; set; }
    public string? Notes { get; set; }
    /// <summary>The revision letter of the latest upload.</summary>
    public string CurrentRevision { get; set; } = "";
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string AuditLabel => $"drawing {(string.IsNullOrEmpty(DrawingNumber) ? "" : DrawingNumber + " ")}{Title}";
}

/// <summary>A drawing linked to a part in Parts and Items (a drawing can belong to several parts and a part can have several drawings).</summary>
public class DrawingPartLink
{
    public Guid DrawingId { get; set; }
    public Guid PartId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A drawing attached to a validation work order (tracking record) when it was created for a part. The revision current at that moment is kept, so the
/// order shows both what was attached and what is current now.
/// </summary>
public class TrackingRecordDrawing
{
    public Guid TrackingRecordId { get; set; }
    public Guid DrawingId { get; set; }
    public string AttachedRevision { get; set; } = "";
    public DateTimeOffset AttachedAt { get; set; }
}

/// <summary>One uploaded file of a drawing.</summary>
public class DrawingRevision : Entity
{
    public Guid DrawingId { get; set; }
    public string Revision { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Notes { get; set; }
    public string CreatedByName { get; set; } = "";
}
