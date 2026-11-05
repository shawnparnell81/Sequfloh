using System.Text.Json.Serialization;
using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Drawings;

/// <summary>Where a drawing revision sits. Pending waits for release. Current is the one in use. Superseded stays on file and is never replaced.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DrawingRevisionStatus
{
    Pending = 0,
    Current = 1,
    Superseded = 2,
}

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
    /// <summary>The drawing number in effect when the work order was created. A later rename does not change it.</summary>
    public string? AttachedDrawingNumber { get; set; }
    /// <summary>The revision file that was current when the work order was created.</summary>
    public Guid? AttachedRevisionId { get; set; }
    public DateTimeOffset AttachedAt { get; set; }
}

/// <summary>One uploaded file of a drawing.</summary>
public class DrawingRevision : Entity
{
    public Guid DrawingId { get; set; }
    public string Revision { get; set; } = "";
    /// <summary>The drawing number in effect when this file was stored. Later edits to the drawing do not change it.</summary>
    public string? DrawingNumber { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>Why this revision was made.</summary>
    public string? Reason { get; set; }
    /// <summary>Optional engineering change number or other change reference.</summary>
    public string? ChangeReference { get; set; }
    public DrawingRevisionStatus Status { get; set; } = DrawingRevisionStatus.Current;
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleasedByName { get; set; }
    public string CreatedByName { get; set; } = "";
}
