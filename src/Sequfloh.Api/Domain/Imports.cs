using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Imports;

/// <summary>Pdf is a completed form kept exactly as uploaded; it opens in the PDF viewer and is never parsed or edited.</summary>
public enum ImportedFileKind { Spreadsheet = 0, Document = 1, Pdf = 2 }

/// <summary>
/// An Excel, CSV or Word file brought into the app. The working copy (xlsx or docx) is opened and edited in the browser; every save is a new
/// version, earlier versions and the file as uploaded stay downloadable.
/// </summary>
public class ImportedFile : Entity, IAudited, IArchivable, ISiteScoped
{
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public ImportedFileKind Kind { get; set; }
    /// <summary>Format of the upload: xlsx, xlsm, xls, csv, docx or doc.</summary>
    public string SourceFormat { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    /// <summary>The file exactly as uploaded (for CSV and legacy formats it differs from the working copy).</summary>
    public string OriginalStorageKey { get; set; } = "";
    public Guid SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public int CurrentVersion { get; set; } = 1;
    /// <summary>What the reader found: sheets, formula counts by class, warnings and security notes (JSON).</summary>
    public string Summary { get; set; } = "{}";
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>A blank form: kept empty and protected from edits; people fill out copies of it.</summary>
    public bool IsBlankForm { get; set; }
    /// <summary>A completed (filled) form imported straight into Completed Validation/FAI, not made from a blank form here.</summary>
    public bool IsCompletedForm { get; set; }
    /// <summary>A completed Salt Spray Test report imported into the Salt Spray Test tab of Completed Validation/FAI (a test, not a product family).</summary>
    public bool IsSaltSprayTest { get; set; }
    /// <summary>For a filled copy: the blank form it came from and the version of that blank it started from.</summary>
    public Guid? BlankFormId { get; set; }
    public int? BlankFormVersion { get; set; }
    /// <summary>The part number a filled copy is for, as typed (it need not be in Parts and Items).</summary>
    public string? PartNumber { get; set; }
    /// <summary>The product family a filled form belongs to (a product-families reference value); null shows it under Unassigned.</summary>
    public Guid? FamilyId { get; set; }
    public List<ImportedFileVersion> Versions { get; set; } = [];
    public string AuditLabel => $"imported file {Number} {Title}";
}

/// <summary>One saved version of the working copy of an imported file.</summary>
public class ImportedFileVersion : Entity
{
    public Guid ImportedFileId { get; set; }
    public int Version { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Note { get; set; }
    public string CreatedByName { get; set; } = "";
}
