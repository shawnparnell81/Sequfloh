using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Shared;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;
using Sequfloh.Infrastructure.Modules.Quality;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record EightDFile(Guid Id, string FileName, string? UploadedBy, DateTimeOffset UploadedAt, string OwnerKind, Guid OwnerId, string? OwnerNumber = null);
public sealed record EightDIncludeInput(bool Include);

/// <summary>
/// 8D reports attached to work orders (every type except Validation and FAI), inspection requests and projects. The files are kept under their own name.
/// Viewing follows the record; uploading and removing need the record to be editable (open), the same lock the record's forms use.
/// </summary>
public sealed class SequflohEightDService(AppDbContext db, IFileStorage storage, ModuleSupport m, SequflohFoundationService f, ICurrentUser me)
{
    public static readonly string[] Kinds = ["work-orders", "inspection-requests", "projects"];
    public static readonly HashSet<string> Allowed = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".csv"];
    public const string NotForValidation = "8D reports are not used on Validation / FAI work orders.";

    public static string OwnerType(string kind) => kind switch
    {
        "work-orders" => "8D:" + SequflohTrackingService.EntityType,
        "inspection-requests" => "8D:" + SequflohInspectionService.EntityType,
        "projects" => "8D:" + SequflohProjectService.EntityType,
        _ => throw new NotFoundException("Record type"),
    };
    static string KindOf(string ownerType) => Kinds.First(k => OwnerType(k) == ownerType);
    public static bool Excluded(TrackingType t) => t is TrackingType.Validation or TrackingType.Fai;

    sealed record Owner(string EntityType, string Label, Guid? SiteId, bool CanView, bool Editable, string? LockReason, Func<bool, Task> SetInclude, bool Include);

    async Task<Owner> OwnerAsync(string kind, Guid id, CancellationToken ct)
    {
        switch (kind)
        {
            case "work-orders":
            {
                var r = await db.SequflohTrackingRecords.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order");
                var can = await m.CanAsync(Permissions.SequflohAccess, r.SiteId, r.DepartmentId, ct);
                var reason = Excluded(r.Type) ? NotForValidation : SequflohTrackingService.IsClosed(r) ? $"{r.Number} is {r.Status.ToString().ToLowerInvariant()} and cannot be changed." : null;
                return new(SequflohTrackingService.EntityType, $"work order {r.Number}", r.SiteId, can, can && reason is null, reason, v => { r.Include8D = v; return Task.CompletedTask; }, r.Include8D);
            }
            case "inspection-requests":
            {
                var r = await db.SequflohInspectionRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Inspection request");
                var can = await m.CanAsync(Permissions.SequflohAccess, r.SiteId, r.DepartmentId, ct);
                var reason = r.Status is InspectionRequestStatus.Done or InspectionRequestStatus.Cancelled ? $"{r.Number} is {(r.Status == InspectionRequestStatus.Done ? "done" : "canceled")} and cannot be changed." : null;
                return new(SequflohInspectionService.EntityType, $"inspection request {r.Number}", r.SiteId, can, can && reason is null, reason, v => { r.Include8D = v; return Task.CompletedTask; }, r.Include8D);
            }
            case "projects":
            {
                var p = await db.SequflohProjects.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Project");
                var can = await m.CanAnyAsync(Permissions.SequflohAccess, ct);
                var manage = await m.CanAnyAsync(Permissions.SequflohManageProjects, ct);
                var reason = p.Status != ProjectStatus.Open ? $"Project {p.Number} is closed and cannot be changed." : null;
                return new(SequflohProjectService.EntityType, $"project {p.Number}", p.SiteId, can, can && manage && reason is null, reason, v => { p.Include8D = v; return Task.CompletedTask; }, p.Include8D);
            }
            default: throw new NotFoundException("Record type");
        }
    }

    async Task<Owner> ViewAsync(string kind, Guid id, CancellationToken ct)
    {
        var o = await OwnerAsync(kind, id, ct);
        if (!o.CanView) throw new NotFoundException("Record");
        return o;
    }

    async Task<Owner> EditAsync(string kind, Guid id, CancellationToken ct)
    {
        var o = await ViewAsync(kind, id, ct);
        if (o.LockReason is { } why) throw new ConflictException(why);
        if (!o.Editable) throw new ForbiddenException();
        return o;
    }

    IQueryable<Attachment> Files(string ownerType, Guid id) => db.Attachments.AsNoTracking().Where(a => a.OwnerType == ownerType && a.OwnerId == id && !a.IsArchived);

    /// <summary>The 8D files on several records of one kind, for the reports.</summary>
    public Task<List<EightDFile>> FilesOfAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct) => FilesOfAsync(db, kind, ids, ct);

    /// <summary>The 8D files on several records of one kind, for the reports. The caller has already checked the records can be seen.</summary>
    public static async Task<List<EightDFile>> FilesOfAsync(AppDbContext db, string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var t = OwnerType(kind);
        var list = await db.Attachments.AsNoTracking().Where(a => a.OwnerType == t && ids.Contains(a.OwnerId) && !a.IsArchived).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var users = list.Select(a => a.CreatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. list.Select(a => new EightDFile(a.Id, a.FileName, a.CreatedById is { } u && names.TryGetValue(u, out var n) ? n : null, a.CreatedAt, kind, a.OwnerId))];
    }

    public async Task<object> ListAsync(string kind, Guid id, CancellationToken ct)
    {
        var o = await ViewAsync(kind, id, ct);
        var files = await FilesOfAsync(kind, [id], ct);
        return new { Include = o.Include || files.Count > 0, o.Editable, o.LockReason, Files = files };
    }

    /// <summary>Checks the extension and that the content looks like that kind of file (PDF, old Office, new Office zip, or plain text for CSV).</summary>
    public static void Check(string fileName, long length, byte[] head)
    {
        if (length == 0) throw new AppException("Choose a file.");
        if (length > FilesController.MaxBytes) throw new AppException("Files are limited to 10 MB.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Allowed.Contains(ext)) throw new AppException("An 8D report must be a PDF, Word (.doc, .docx) or Excel (.xls, .xlsx, .xlsm, .csv) file.");
        bool Starts(params byte[] sig) => head.Length >= sig.Length && head.AsSpan(0, sig.Length).SequenceEqual(sig);
        var ok = ext switch
        {
            ".pdf" => Starts(0x25, 0x50, 0x44, 0x46),
            ".doc" or ".xls" => Starts(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1),
            ".docx" or ".xlsx" or ".xlsm" => Starts(0x50, 0x4B, 0x03, 0x04),
            _ => !head.Contains((byte)0) && !Starts(0x4D, 0x5A),
        };
        if (!ok) throw new AppException($"{Path.GetFileName(fileName)} is not a real {ext[1..].ToUpperInvariant()} file.");
    }

    static string Mime(string ext) => ext switch
    {
        ".pdf" => "application/pdf", ".doc" => "application/msword", ".xls" => "application/vnd.ms-excel", ".csv" => "text/csv",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/vnd.ms-excel.sheet.macroEnabled.12",
    };

    public async Task<object> UploadAsync(string kind, Guid id, string fileName, Stream content, CancellationToken ct)
    {
        var o = await EditAsync(kind, id, ct);
        var name = Path.GetFileName(fileName ?? "").Trim();
        if (name.Length == 0) throw new AppException("Choose a file.");
        if (name.Length > 255) throw new AppException("The file name is too long.");
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        Check(name, bytes.Length, bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).ToArray());
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var a = new Attachment { OwnerType = OwnerType(kind), OwnerId = id, SiteId = o.SiteId, FileName = name, ContentType = Mime(ext), SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        a.StorageKey = $"8d/{kind}/{id:N}/{a.Id:N}{ext}";
        ms.Position = 0;
        await storage.PutAsync(a.StorageKey, ms, a.ContentType, ct);
        db.Attachments.Add(a);
        await o.SetInclude(true);
        f.Audit("EightDUploaded", o.EntityType, id, $"8D report {name} added to {o.Label}", o.SiteId);
        await db.SaveChangesAsync(ct);
        return new { a.Id, a.FileName };
    }

    public async Task RemoveAsync(Guid fileId, CancellationToken ct)
    {
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == fileId && x.OwnerType.StartsWith("8D:"), ct) ?? throw new NotFoundException("8D report");
        var o = await EditAsync(KindOf(a.OwnerType), a.OwnerId, ct);
        if (a.IsArchived) return;
        a.IsArchived = true; a.ArchivedAt = m.Now;
        f.Audit("EightDRemoved", o.EntityType, a.OwnerId, $"8D report {a.FileName} removed from {o.Label}", o.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(Guid fileId, CancellationToken ct)
    {
        var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.OwnerType.StartsWith("8D:") && !x.IsArchived, ct) ?? throw new NotFoundException("8D report");
        await ViewAsync(KindOf(a.OwnerType), a.OwnerId, ct);
        var s = await storage.GetAsync(a.StorageKey, ct) ?? throw new NotFoundException("File content");
        return (s, a.ContentType, a.FileName);
    }

    /// <summary>Turns the 8D Report block on or off for a record. Off is refused while 8D files are attached.</summary>
    public async Task SetIncludeAsync(string kind, Guid id, bool include, CancellationToken ct)
    {
        var o = await EditAsync(kind, id, ct);
        if (o.Include == include) return;
        if (!include && await Files(OwnerType(kind), id).AnyAsync(ct)) throw new AppException("Remove the 8D files first, then choose No.");
        await o.SetInclude(include);
        f.Audit("EightDIncluded", o.EntityType, id, include ? $"8D Report turned on for {o.Label}" : $"8D Report turned off for {o.Label}", o.SiteId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>For the project form: refuses No while the project has 8D files.</summary>
    public async Task<bool> HasFilesAsync(string kind, Guid id, CancellationToken ct) => await Files(OwnerType(kind), id).AnyAsync(ct);
}
