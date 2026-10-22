using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;

namespace Sequfloh.Drawings;

/// <summary>
/// Drawing folders (optionally linked to a part, nested, archived rather than deleted) and the drawings in them. Each upload is a revision kept in file storage;
/// PDF and common images can be viewed in the browser, other CAD files download.
/// </summary>
public sealed class DrawingService(AppDbContext db, IFileStorage storage, IAccessService access, ICurrentUser me)
{
    public const long MaxBytes = 50 * 1024 * 1024;
    public static readonly string[] Accepted = [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".dwg", ".dxf", ".step", ".stp", ".iges", ".igs", ".slddrw", ".sldprt"];
    /// <summary>Formats a browser can show on its own.</summary>
    static readonly HashSet<string> Viewable = [".pdf", ".png", ".jpg", ".jpeg"];

    Guid UserId => me.UserId ?? throw new ForbiddenException();

    // ---- access -------------------------------------------------------------------------------------------------------------------

    async Task<T> CheckAsync<T>(T x, string permission, CancellationToken ct) where T : class, Sequfloh.Domain.Common.ISiteScoped
    {
        if (!await access.CanAccessAsync(UserId, Permissions.SequflohViewDrawings, x.SiteId, x.DepartmentId, ct)) throw new NotFoundException(x is Drawing ? "Drawing" : "Folder");
        if (permission != Permissions.SequflohViewDrawings && !await access.CanAccessAsync(UserId, permission, x.SiteId, x.DepartmentId, ct)) throw new ForbiddenException();
        return x;
    }

    async Task<DrawingFolder> FolderAsync(Guid id, string permission, CancellationToken ct) =>
        await CheckAsync(await db.DrawingFolders.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("Folder"), permission, ct);

    async Task<Drawing> DrawingAsync(Guid id, string permission, CancellationToken ct) =>
        await CheckAsync(await db.Drawings.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Drawing"), permission, ct);

    async Task<AccessScope> ScopeAsync(CancellationToken ct) => await access.GetScopeAsync(UserId, Permissions.SequflohViewDrawings, ct);

    void Audit(Guid entityId, Guid siteId, string action, string text) => db.AuditEntries.Add(new AuditEntry
    {
        OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = action, EntityType = nameof(Drawing), EntityId = entityId, SiteId = siteId,
        CorrelationId = me.CorrelationId, Description = AuditText.Fit(text),
    });

    static string Clean(string? s, int max, string what, bool required = true)
    {
        var t = (s ?? "").Trim();
        if (required && t.Length == 0) throw new AppException($"Enter {what}.");
        if (t.Length > max) throw new AppException($"Keep {what} to {max} characters.");
        return t;
    }

    // ---- folders ------------------------------------------------------------------------------------------------------------------

    /// <summary>The folders directly inside a folder (or at the top), with what they hold.</summary>
    public async Task<object> FoldersAsync(Guid? parentId, bool archived, CancellationToken ct)
    {
        object? parent = null; List<object> path = [];
        if (parentId is { } pid)
        {
            var p = await FolderAsync(pid, Permissions.SequflohViewDrawings, ct);
            var canManage = await access.CanAccessAsync(UserId, Permissions.SequflohManageDrawings, p.SiteId, p.DepartmentId, ct);
            var part = p.PartId is null ? null : await db.Parts.AsNoTracking().Where(x => x.Id == p.PartId).Select(x => new { x.Id, x.PartNumber, x.Name }).FirstOrDefaultAsync(ct);
            parent = new { p.Id, p.Name, p.ParentId, p.IsArchived, Part = part, CanManage = canManage };
            for (Guid? at = p.ParentId; at is not null && path.Count < 50;)
            {
                var up = await db.DrawingFolders.AsNoTracking().Where(f => f.Id == at).Select(f => new { f.Id, f.Name, f.ParentId }).FirstOrDefaultAsync(ct);
                if (up is null) break;
                path.Insert(0, new { up.Id, up.Name }); at = up.ParentId;
            }
        }
        var scope = await ScopeAsync(ct);
        var rows = scope.IsEmpty ? [] : await db.DrawingFolders.AsNoTracking().Where(f => f.ParentId == parentId && f.IsArchived == archived).OrderBy(f => f.Name)
            .Select(f => new
            {
                f.Id, f.Name, f.SiteId, f.DepartmentId, f.IsArchived, f.UpdatedAt,
                Part = db.Parts.Where(x => x.Id == f.PartId).Select(x => new { x.Id, x.PartNumber, x.Name }).FirstOrDefault(),
                Folders = db.DrawingFolders.Count(c => c.ParentId == f.Id && !c.IsArchived),
                Drawings = db.Drawings.Count(d => d.FolderId == f.Id && !d.IsArchived),
            }).ToListAsync(ct);
        var folders = rows.Where(r => scope.Allows(r.SiteId, r.DepartmentId)).Select(r => new { r.Id, r.Name, r.IsArchived, r.UpdatedAt, r.Part, r.Folders, r.Drawings }).ToList();
        var drawings = parentId is null ? [] : await DrawingRowsAsync(db.Drawings.Where(d => d.FolderId == parentId && d.IsArchived == archived), scope, ct);
        var canCreate = parentId is null ? await access.HasPermissionAsync(UserId, Permissions.SequflohManageDrawings, ct) : false;
        return new { Parent = parent, Path = path, Folders = folders, Drawings = drawings, CanCreate = canCreate };
    }

    public sealed record FolderRequest(string Name, Guid? ParentId, Guid? PartId, Guid? SiteId = null);

    public async Task<object> CreateFolderAsync(FolderRequest r, CancellationToken ct)
    {
        var name = Clean(r.Name, 200, "a folder name");
        Guid site; Guid? dept = null;
        if (r.ParentId is { } pid)
        {
            var parent = await FolderAsync(pid, Permissions.SequflohManageDrawings, ct);
            if (parent.IsArchived) throw new AppException("The folder is archived. Restore it first.");
            site = parent.SiteId; dept = parent.DepartmentId;
        }
        else
        {
            // The chosen site, else the user's default site, else the first site where they may manage drawings.
            var candidates = new List<Guid>();
            if (r.SiteId is { } chosen) candidates.Add(chosen);
            else
            {
                if (await db.Users.Where(u => u.Id == UserId).Select(u => u.DefaultSiteId).FirstOrDefaultAsync(ct) is { } d) candidates.Add(d);
                candidates.AddRange(await db.Sites.OrderBy(s => s.Code).Select(s => s.Id).ToListAsync(ct));
            }
            if (candidates.Count == 0) throw new AppException("Set up a site first.");
            Guid? found = null;
            foreach (var c in candidates) if (await access.CanAccessAsync(UserId, Permissions.SequflohManageDrawings, c, null, ct)) { found = c; break; }
            site = found ?? throw new ForbiddenException();
        }
        await CheckPartAsync(r.PartId, ct);
        if (await db.DrawingFolders.AnyAsync(f => f.ParentId == r.ParentId && f.Name == name && !f.IsArchived && f.SiteId == site, ct)) throw new ConflictException("A folder with this name is already here.");
        var f = new DrawingFolder { Name = name, ParentId = r.ParentId, PartId = r.PartId, SiteId = site, DepartmentId = dept };
        db.DrawingFolders.Add(f);
        await db.SaveChangesAsync(ct);
        return new { f.Id, f.Name };
    }

    async Task CheckPartAsync(Guid? partId, CancellationToken ct)
    {
        if (partId is { } id && !await db.Parts.AnyAsync(p => p.Id == id, ct)) throw new AppException("That part was not found in Parts and Items.");
    }

    public async Task UpdateFolderAsync(Guid id, FolderRequest r, CancellationToken ct)
    {
        var f = await FolderAsync(id, Permissions.SequflohManageDrawings, ct);
        var name = Clean(r.Name, 200, "a folder name");
        await CheckPartAsync(r.PartId, ct);
        if (name != f.Name && await db.DrawingFolders.AnyAsync(x => x.ParentId == f.ParentId && x.Name == name && !x.IsArchived && x.Id != f.Id && x.SiteId == f.SiteId, ct))
            throw new ConflictException("A folder with this name is already here.");
        f.Name = name; f.PartId = r.PartId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Archives or restores a folder. Archived folders and what is in them are hidden from the normal view and from search; nothing is deleted.</summary>
    public async Task ArchiveFolderAsync(Guid id, bool archive, CancellationToken ct)
    {
        var f = await FolderAsync(id, Permissions.SequflohManageDrawings, ct);
        if (!archive && f.ParentId is { } pid && await db.DrawingFolders.AnyAsync(p => p.Id == pid && p.IsArchived, ct)) throw new AppException("Restore the folder it is in first.");
        f.IsArchived = archive; f.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    // ---- drawings -----------------------------------------------------------------------------------------------------------------

    async Task<List<object>> DrawingRowsAsync(IQueryable<Drawing> q, AccessScope scope, CancellationToken ct, int take = 500)
    {
        var rows = await q.AsNoTracking().OrderBy(d => d.DrawingNumber).ThenBy(d => d.Title).Take(take)
            .Select(d => new
            {
                d.Id, d.Title, d.DrawingNumber, d.CurrentRevision, d.FolderId, d.SiteId, d.DepartmentId, d.IsArchived, d.UpdatedAt,
                Folder = db.DrawingFolders.Where(f => f.Id == d.FolderId).Select(f => f.Name).FirstOrDefault(),
                PartNumber = db.DrawingFolders.Where(f => f.Id == d.FolderId).SelectMany(f => db.Parts.Where(p => p.Id == f.PartId).Select(p => p.PartNumber)).FirstOrDefault()
                    ?? db.DrawingPartLinks.Where(l => l.DrawingId == d.Id).SelectMany(l => db.Parts.Where(p => p.Id == l.PartId).Select(p => p.PartNumber)).OrderBy(x => x).FirstOrDefault(),
                File = db.DrawingRevisions.Where(v => v.DrawingId == d.Id && v.Revision == d.CurrentRevision).Select(v => new { v.FileName, v.SizeBytes, v.CreatedByName, v.CreatedAt }).FirstOrDefault(),
            }).ToListAsync(ct);
        return rows.Where(r => scope.Allows(r.SiteId, r.DepartmentId)).Select(r => (object)new { r.Id, r.Title, r.DrawingNumber, r.CurrentRevision, r.FolderId, r.Folder, r.PartNumber, r.IsArchived, r.UpdatedAt, r.File }).ToList();
    }

    /// <summary>Finds drawings by part number (a linked part, the folder's part or the folder name), drawing number or title. Archived drawings and folders are left out.</summary>
    public async Task<object> SearchAsync(string? q, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty || string.IsNullOrWhiteSpace(q)) return Array.Empty<object>();
        var t = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var live = db.DrawingFolders.Where(f => !f.IsArchived);
        var query = db.Drawings.Where(d => !d.IsArchived && live.Any(f => f.Id == d.FolderId) && (EF.Functions.ILike(d.Title, t) || EF.Functions.ILike(d.DrawingNumber ?? "", t)
            || db.DrawingFolders.Any(f => f.Id == d.FolderId && (EF.Functions.ILike(f.Name, t) || db.Parts.Any(p => p.Id == f.PartId && (EF.Functions.ILike(p.PartNumber, t) || EF.Functions.ILike(p.Name, t)))))
            || db.DrawingPartLinks.Any(l => l.DrawingId == d.Id && db.Parts.Any(p => p.Id == l.PartId && (EF.Functions.ILike(p.PartNumber, t) || EF.Functions.ILike(p.Name, t))))));
        return await DrawingRowsAsync(query, scope, ct, 200);
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var d = await DrawingAsync(id, Permissions.SequflohViewDrawings, ct);
        var folder = await db.DrawingFolders.AsNoTracking().Where(f => f.Id == d.FolderId).Select(f => new { f.Id, f.Name, f.IsArchived,
            Part = db.Parts.Where(p => p.Id == f.PartId).Select(p => new { p.Id, p.PartNumber, p.Name }).FirstOrDefault() }).FirstAsync(ct);
        var revisions = await db.DrawingRevisions.AsNoTracking().Where(v => v.DrawingId == id).OrderByDescending(v => v.CreatedAt)
            .Select(v => new { v.Id, v.Revision, v.FileName, v.ContentType, v.SizeBytes, v.Notes, v.CreatedAt, v.CreatedByName }).ToListAsync(ct);
        var canManage = await access.CanAccessAsync(UserId, Permissions.SequflohManageDrawings, d.SiteId, d.DepartmentId, ct);
        var parts = await db.DrawingPartLinks.AsNoTracking().Where(l => l.DrawingId == id).Join(db.Parts, l => l.PartId, p => p.Id, (l, p) => new { p.Id, p.PartNumber, p.Name })
            .OrderBy(p => p.PartNumber).ToListAsync(ct);
        return new
        {
            d.Id, d.Title, d.DrawingNumber, d.Notes, d.CurrentRevision, d.IsArchived, d.CreatedAt, d.UpdatedAt, Folder = folder, CanManage = canManage, Parts = parts,
            Revisions = revisions.Select(v => new { v.Id, v.Revision, v.FileName, v.ContentType, v.SizeBytes, v.Notes, v.CreatedAt, v.CreatedByName,
                Viewable = Viewable.Contains(Path.GetExtension(v.FileName).ToLowerInvariant()), Current = v.Revision == d.CurrentRevision }),
        };
    }

    public sealed record DrawingFields(string? Title, string? DrawingNumber, string? Revision, string? Notes);

    public async Task<object> UploadAsync(Guid folderId, Stream content, string fileName, DrawingFields r, CancellationToken ct)
    {
        var folder = await FolderAsync(folderId, Permissions.SequflohManageDrawings, ct);
        if (folder.IsArchived) throw new AppException("The folder is archived. Restore it first.");
        var (bytes, name, ext) = await ReadFileAsync(content, fileName, ct);
        var title = Clean(string.IsNullOrWhiteSpace(r.Title) ? Path.GetFileNameWithoutExtension(name) : r.Title, 300, "a title");
        var number = Clean(r.DrawingNumber, 100, "a drawing number", false);
        var rev = Revision(r.Revision);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var d = new Drawing { FolderId = folder.Id, Title = title, DrawingNumber = number.Length == 0 ? null : number, Notes = Clean(r.Notes, 4000, "notes", false) is { Length: > 0 } n ? n : null,
            CurrentRevision = rev, SiteId = folder.SiteId, DepartmentId = folder.DepartmentId };
        db.Drawings.Add(d);
        await db.SaveChangesAsync(ct);
        await StoreRevisionAsync(d, rev, bytes, name, ext, null, ct);
        Audit(d.Id, d.SiteId, "Uploaded revision", $"Uploaded {d.AuditLabel} revision {rev} ({name}) to folder {folder.Name}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { d.Id, d.Title };
    }

    /// <summary>Adds a new revision; the earlier ones are kept.</summary>
    public async Task<object> UploadRevisionAsync(Guid id, Stream content, string fileName, string? revision, string? notes, CancellationToken ct)
    {
        var d = await DrawingAsync(id, Permissions.SequflohManageDrawings, ct);
        if (d.IsArchived) throw new AppException("The drawing is archived. Restore it first.");
        var (bytes, name, ext) = await ReadFileAsync(content, fileName, ct);
        var rev = Revision(revision);
        if (await db.DrawingRevisions.AnyAsync(v => v.DrawingId == id && v.Revision == rev, ct)) throw new ConflictException($"Revision {rev} is already uploaded. Use the next revision letter.");
        var previous = d.CurrentRevision;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StoreRevisionAsync(d, rev, bytes, name, ext, Clean(notes, 2000, "revision notes", false) is { Length: > 0 } n ? n : null, ct);
        d.CurrentRevision = rev;
        Audit(d.Id, d.SiteId, "Uploaded revision", $"Uploaded {d.AuditLabel} revision {rev} ({name}); revision {previous} is kept in the history");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { Revision = rev };
    }

    static string Revision(string? r)
    {
        var rev = (r ?? "").Trim().ToUpperInvariant();
        if (rev.Length == 0) throw new AppException("Enter the revision letter.");
        if (rev.Length > 20 || rev.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '.')) throw new AppException("Use letters, numbers, '-' or '.' for the revision (up to 20).");
        return rev;
    }

    async Task<(byte[] Bytes, string Name, string Ext)> ReadFileAsync(Stream content, string fileName, CancellationToken ct)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!Accepted.Contains(ext)) throw new AppException("Upload a PDF, PNG, JPG, TIF, DWG, DXF, STEP, IGES or SolidWorks (SLDDRW, SLDPRT) file.");
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        if (ms.Length == 0) throw new AppException("The file is empty.");
        if (ms.Length > MaxBytes) throw new AppException("Drawings are limited to 50 MB.");
        var b = ms.ToArray();
        if (!Matches(b, ext)) throw new AppException($"This file does not look like a {ext.TrimStart('.').ToUpperInvariant()} file.");
        return (b, name, ext);
    }

    /// <summary>Checks the first bytes of formats that have a fixed signature, so a renamed file is not shown as something it is not.</summary>
    static bool Matches(byte[] b, string ext)
    {
        static bool Starts(byte[] b, params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);
        return ext switch
        {
            ".pdf" => Starts(b, "%PDF-"u8.ToArray()),
            ".png" => Starts(b, 0x89, 0x50, 0x4E, 0x47),
            ".jpg" or ".jpeg" => Starts(b, 0xFF, 0xD8, 0xFF),
            ".tif" or ".tiff" => Starts(b, 0x49, 0x49, 0x2A, 0x00) || Starts(b, 0x4D, 0x4D, 0x00, 0x2A),
            ".dwg" => Starts(b, "AC10"u8.ToArray()),
            _ => true,
        };
    }

    static string TypeOf(string ext) => ext switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".tif" or ".tiff" => "image/tiff", ".dwg" => "image/vnd.dwg", ".dxf" => "image/vnd.dxf",
        ".step" or ".stp" => "model/step", ".iges" or ".igs" => "model/iges", _ => "application/octet-stream",
    };

    async Task StoreRevisionAsync(Drawing d, string rev, byte[] bytes, string name, string ext, string? notes, CancellationToken ct)
    {
        var v = new DrawingRevision { DrawingId = d.Id, Revision = rev, FileName = name, ContentType = TypeOf(ext), SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Notes = notes, CreatedByName = me.UserName };
        v.StorageKey = $"drawings/{d.Id:N}/{v.Id:N}{ext}";
        await storage.PutAsync(v.StorageKey, new MemoryStream(bytes), v.ContentType, ct);
        db.DrawingRevisions.Add(v);
    }

    public async Task UpdateAsync(Guid id, DrawingFields r, CancellationToken ct)
    {
        var d = await DrawingAsync(id, Permissions.SequflohManageDrawings, ct);
        d.Title = Clean(r.Title, 300, "a title");
        var number = Clean(r.DrawingNumber, 100, "a drawing number", false);
        d.DrawingNumber = number.Length == 0 ? null : number;
        var notes = Clean(r.Notes, 4000, "notes", false);
        d.Notes = notes.Length == 0 ? null : notes;
        await db.SaveChangesAsync(ct);
    }

    public async Task ArchiveAsync(Guid id, bool archive, CancellationToken ct)
    {
        var d = await DrawingAsync(id, Permissions.SequflohManageDrawings, ct);
        d.IsArchived = archive; d.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Stream Content, string ContentType, string FileName, bool Viewable)> FileAsync(Guid id, string? revision, CancellationToken ct)
    {
        var d = await DrawingAsync(id, Permissions.SequflohViewDrawings, ct);
        var rev = string.IsNullOrWhiteSpace(revision) ? d.CurrentRevision : revision.Trim().ToUpperInvariant();
        var v = await db.DrawingRevisions.AsNoTracking().FirstOrDefaultAsync(x => x.DrawingId == id && x.Revision == rev, ct) ?? throw new NotFoundException("Revision");
        var s = await storage.GetAsync(v.StorageKey, ct) ?? throw new NotFoundException("File content");
        return (s, v.ContentType, v.FileName, Viewable.Contains(Path.GetExtension(v.FileName).ToLowerInvariant()));
    }

    // ---- parts --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The current revision of every drawing for a part: drawings linked to it and drawings in a folder linked to it. Looked up by part id or by the part number
    /// as typed; an unknown part number, or a user who cannot view drawings, simply gets an empty list.
    /// </summary>
    public async Task<List<object>> ForPartAsync(Guid? partId, string? partNumber, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        List<Guid> parts;
        if (partId is { } pid) parts = [pid];
        else if (!string.IsNullOrWhiteSpace(partNumber))
        {
            var n = partNumber.Trim().ToUpperInvariant();
            parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.ToUpper() == n).Select(p => p.Id).ToListAsync(ct);
        }
        else return [];
        if (parts.Count == 0) return [];
        var rows = await PartDrawings(db, parts).AsNoTracking()
            .OrderBy(d => d.DrawingNumber).ThenBy(d => d.Title).Take(50)
            .Select(d => new { d.Id, d.Title, d.DrawingNumber, d.CurrentRevision, d.SiteId, d.DepartmentId,
                FileName = db.DrawingRevisions.Where(v => v.DrawingId == d.Id && v.Revision == d.CurrentRevision).Select(v => v.FileName).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.Where(r => scope.Allows(r.SiteId, r.DepartmentId)).Select(r => (object)new { r.Id, r.Title, r.DrawingNumber, r.CurrentRevision, r.FileName,
            Viewable = Viewable.Contains(Path.GetExtension(r.FileName ?? "").ToLowerInvariant()) }).ToList();
    }

    /// <summary>Drawings for a set of parts: linked directly or kept in a folder linked to the part. Archived drawings and folders are left out.</summary>
    internal static IQueryable<Drawing> PartDrawings(AppDbContext db, ICollection<Guid> parts)
    {
        var folders = db.DrawingFolders.Where(f => !f.IsArchived && f.PartId != null && parts.Contains(f.PartId.Value)).Select(f => f.Id);
        return db.Drawings.Where(d => !d.IsArchived && db.DrawingFolders.Any(f => f.Id == d.FolderId && !f.IsArchived)
            && (db.DrawingPartLinks.Any(l => l.DrawingId == d.Id && parts.Contains(l.PartId)) || folders.Contains(d.FolderId)));
    }

    /// <summary>Attaches the part's current drawings to a new validation work order (called while the order is being created, before it is saved).</summary>
    internal static async Task AttachToRecordAsync(AppDbContext db, Guid recordId, Guid partId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var d in await PartDrawings(db, [partId]).AsNoTracking().Select(d => new { d.Id, d.CurrentRevision }).ToListAsync(ct))
            db.TrackingRecordDrawings.Add(new TrackingRecordDrawing { TrackingRecordId = recordId, DrawingId = d.Id, AttachedRevision = d.CurrentRevision, AttachedAt = now });
    }

    /// <summary>The drawings attached to a work order, with the revision attached and the revision current now.</summary>
    public async Task<List<object>> ForRecordAsync(Guid recordId, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var rows = await db.TrackingRecordDrawings.AsNoTracking().Where(a => a.TrackingRecordId == recordId)
            .Join(db.Drawings, a => a.DrawingId, d => d.Id, (a, d) => new { d.Id, d.Title, d.DrawingNumber, d.CurrentRevision, d.SiteId, d.DepartmentId, d.IsArchived, a.AttachedRevision, a.AttachedAt,
                FileName = db.DrawingRevisions.Where(v => v.DrawingId == d.Id && v.Revision == d.CurrentRevision).Select(v => v.FileName).FirstOrDefault() })
            .OrderBy(x => x.DrawingNumber).ThenBy(x => x.Title).ToListAsync(ct);
        return rows.Where(r => scope.Allows(r.SiteId, r.DepartmentId)).Select(r => (object)new { r.Id, r.Title, r.DrawingNumber, r.CurrentRevision, r.AttachedRevision, r.AttachedAt, r.IsArchived,
            r.FileName, Viewable = Viewable.Contains(Path.GetExtension(r.FileName ?? "").ToLowerInvariant()) }).ToList();
    }

    async Task<Sequfloh.Domain.MasterData.Part> PartAsync(Guid id, CancellationToken ct) =>
        await db.Parts.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new AppException("That part was not found in Parts and Items.");

    /// <summary>Sets which drawings are linked to a part. Only links the user can manage are changed.</summary>
    public async Task SetPartDrawingsAsync(Guid partId, IReadOnlyList<Guid> drawingIds, CancellationToken ct)
    {
        var part = await PartAsync(partId, ct);
        var want = drawingIds.Distinct().ToHashSet();
        var have = await db.DrawingPartLinks.Where(l => l.PartId == partId).ToListAsync(ct);
        foreach (var l in have.Where(l => !want.Contains(l.DrawingId)))
        {
            var d = await DrawingAsync(l.DrawingId, Permissions.SequflohManageDrawings, ct);
            db.DrawingPartLinks.Remove(l);
            Audit(d.Id, d.SiteId, "Unlinked from part", $"Unlinked {d.AuditLabel} from part {part.PartNumber}");
        }
        foreach (var id in want.Where(id => have.All(l => l.DrawingId != id)))
        {
            var d = await DrawingAsync(id, Permissions.SequflohManageDrawings, ct);
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = id, PartId = partId, CreatedAt = DateTimeOffset.UtcNow });
            Audit(d.Id, d.SiteId, "Linked to part", $"Linked {d.AuditLabel} to part {part.PartNumber}");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sets which parts a drawing is linked to.</summary>
    public async Task SetDrawingPartsAsync(Guid drawingId, IReadOnlyList<Guid> partIds, CancellationToken ct)
    {
        var d = await DrawingAsync(drawingId, Permissions.SequflohManageDrawings, ct);
        var want = partIds.Distinct().ToHashSet();
        var have = await db.DrawingPartLinks.Where(l => l.DrawingId == drawingId).ToListAsync(ct);
        foreach (var l in have.Where(l => !want.Contains(l.PartId)))
        {
            var p = await db.Parts.AsNoTracking().Where(x => x.Id == l.PartId).Select(x => x.PartNumber).FirstOrDefaultAsync(ct);
            db.DrawingPartLinks.Remove(l);
            Audit(d.Id, d.SiteId, "Unlinked from part", $"Unlinked {d.AuditLabel} from part {p}");
        }
        foreach (var id in want.Where(id => have.All(l => l.PartId != id)))
        {
            var p = await PartAsync(id, ct);
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = drawingId, PartId = id, CreatedAt = DateTimeOffset.UtcNow });
            Audit(d.Id, d.SiteId, "Linked to part", $"Linked {d.AuditLabel} to part {p.PartNumber}");
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Uploads a drawing straight from a part: it goes into the part's drawing folder (made at the top level, named after the part, when there is none) and is linked to the part.</summary>
    public async Task<object> UploadForPartAsync(Guid partId, Stream content, string fileName, DrawingFields r, CancellationToken ct)
    {
        var part = await PartAsync(partId, ct);
        var folder = await db.DrawingFolders.Where(f => f.PartId == partId && !f.IsArchived).OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(ct);
        if (folder is null)
        {
            if (!await access.CanAccessAsync(UserId, Permissions.SequflohManageDrawings, part.SiteId, null, ct)) throw new ForbiddenException();
            folder = new DrawingFolder { Name = part.PartNumber, PartId = part.Id, SiteId = part.SiteId };
            db.DrawingFolders.Add(folder);
            await db.SaveChangesAsync(ct);
        }
        var created = await UploadAsync(folder.Id, content, fileName, r with { DrawingNumber = string.IsNullOrWhiteSpace(r.DrawingNumber) ? null : r.DrawingNumber }, ct);
        var id = (Guid)created.GetType().GetProperty("Id")!.GetValue(created)!;
        db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = id, PartId = partId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Who did what and when to a drawing or a folder, newest first.</summary>
    public async Task<object> HistoryAsync(Guid id, bool folder, CancellationToken ct)
    {
        if (folder) await FolderAsync(id, Permissions.SequflohViewDrawings, ct); else await DrawingAsync(id, Permissions.SequflohViewDrawings, ct);
        return await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id).OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(200)
            .Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Reason }).ToListAsync(ct);
    }
}
