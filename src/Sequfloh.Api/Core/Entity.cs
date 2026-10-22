namespace Sequfloh.Domain.Common;

/// <summary>Base for all persisted records. RowVersion maps to the PostgreSQL xmin column for optimistic concurrency.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>Records that are archived instead of deleted.</summary>
public interface IArchivable
{
    bool IsArchived { get; set; }
    DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>Records whose changes are written to the business audit trail.</summary>
public interface IAudited
{
    /// <summary>Human-readable identifier used in audit descriptions, such as a record number.</summary>
    string AuditLabel { get; }
}

/// <summary>Records owned by a site (and optionally a department) for scope-based access.</summary>
public interface ISiteScoped
{
    Guid SiteId { get; }
    Guid? DepartmentId { get; }
}

/// <summary>Ledger records that can be added but never changed or deleted (corrections are new entries).</summary>
public interface IAppendOnly { }

/// <summary>
/// Reference to another record, person, role or site inside an audit label. The audit writer replaces it with that record's
/// readable name (a record number, a person's name, a role or site), or the fallback text when it can't be found, so audit
/// descriptions never show raw ids.
/// </summary>
public static class AuditRef
{
    public static string To(Guid? id, string fallback) => id is { } g ? $"\u27E6{g:D}|{fallback}\u27E7" : fallback;
}

/// <summary>Records that can be deleted from the screens; reserved for a future delete-with-restore feature.</summary>
public interface ISoftDeletable { }
