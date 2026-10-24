using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Shared;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Platform;

[ApiController, Route("api/audit-trail")]
public sealed class AuditTrailController(AppDbContext db) : ControllerBase
{
    /// <summary>Who did what, when, with a plain description, newest first.</summary>
    [HttpGet, RequirePermission(Permissions.AuditTrailView)]
    public async Task<object> List(string? q, string? action, Guid? userId, DateOnly? from, DateOnly? to, string? entityType = null, Guid? entityId = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var query = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(a => EF.Functions.ILike(a.Description, "%" + t + "%") || EF.Functions.ILike(a.UserName, "%" + t + "%")); }
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (userId is { } u) query = query.Where(a => a.UserId == u);
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (entityId is { } rec) query = query.Where(a => a.EntityId == rec);
        if (from is { } f) { var s = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); query = query.Where(a => a.OccurredAt >= s); }
        if (to is { } tt) { var e = new DateTimeOffset(tt.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); query = query.Where(a => a.OccurredAt < e); }
        pageSize = Math.Clamp(pageSize, 1, 200); page = Math.Max(1, page);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new { a.Id, Who = a.UserName, What = a.Action, When = a.OccurredAt, a.Description, a.EntityType, a.EntityId }).ToListAsync(ct);
        return new { total, items };
    }

    /// <summary>The record types and actions in the audit trail, for the filters.</summary>
    [HttpGet("filters"), RequirePermission(Permissions.AuditTrailView)]
    public async Task<object> Filters(CancellationToken ct) => new
    {
        Types = await db.AuditEntries.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t).Take(500).ToListAsync(ct),
        Actions = await db.AuditEntries.AsNoTracking().Select(a => a.Action).Distinct().OrderBy(t => t).Take(500).ToListAsync(ct),
    };
}

[ApiController, Route("api/notifications")]
public sealed class NotificationsController(AppDbContext db, ICurrentUser me) : ControllerBase
{
    [HttpGet]
    public async Task<object> Mine(CancellationToken ct) =>
        await db.Notifications.AsNoTracking().Where(n => n.UserId == me.UserId).OrderByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new { n.Id, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt }).ToListAsync(ct);

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await db.Notifications.Where(n => n.Id == id && n.UserId == me.UserId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
        return NoContent();
    }
}

/// <summary>Files attached to work instructions and tracking records (pictures for instruction blocks, evidence).</summary>
[ApiController]
public sealed class FilesController(AppDbContext db, IFileStorage storage, IAccessService access, ICurrentUser me) : ControllerBase
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public static readonly HashSet<string> Allowed = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".csv", ".xlsx", ".docx"];

    /// <summary>Checks the file (type and size) and throws with the reason when it cannot be attached.</summary>
    public static void Check(IFormFile? file)
    {
        if (file is null || file.Length == 0) throw new AppException("Choose a file.");
        if (file.Length > MaxBytes) throw new AppException("Files are limited to 10 MB.");
        if (!Allowed.Contains(Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant()))
            throw new AppException("This file type is not accepted. Use a picture, PDF, text, CSV, Excel or Word file.");
    }

    /// <summary>Stores the file and attaches it to the record. The attachment is audited like any other change.</summary>
    public static async Task<Attachment> AttachAsync(AppDbContext db, IFileStorage storage, string type, Guid id, Guid site, IFormFile file, CancellationToken ct)
    {
        Check(file);
        var name = Path.GetFileName(file.FileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
        var a = new Attachment { OwnerType = type, OwnerId = id, SiteId = site, FileName = name, ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length, Sha256 = hash };
        a.StorageKey = $"{type}/{id:N}/{a.Id:N}{ext}";
        ms.Position = 0;
        await storage.PutAsync(a.StorageKey, ms, a.ContentType, ct);
        db.Attachments.Add(a);
        await db.SaveChangesAsync(ct);
        return a;
    }

    async Task<(Guid SiteId, Guid? DepartmentId, bool Editable)> OwnerAsync(string type, Guid id, CancellationToken ct)
    {
        switch (type)
        {
            case "SequflohWorkInstruction":
            {
                var w = await db.SequflohWorkInstructions.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.SiteId, x.DepartmentId }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Record");
                return (w.SiteId, w.DepartmentId, await access.CanAccessAsync(me.UserId!.Value, Permissions.SequflohEditDraftWorkInstruction, w.SiteId, w.DepartmentId, ct));
            }
            case "SequflohInspectionRequest":
            {
                var t = await db.SequflohInspectionRequests.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.SiteId, x.DepartmentId }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Record");
                return (t.SiteId, t.DepartmentId, await access.CanAccessAsync(me.UserId!.Value, Permissions.SequflohAccess, t.SiteId, t.DepartmentId, ct));
            }
            case "SequflohTrackingRecord":
            {
                var t = await db.SequflohTrackingRecords.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.SiteId, x.DepartmentId }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Record");
                return (t.SiteId, t.DepartmentId, await access.CanAccessAsync(me.UserId!.Value, Permissions.SequflohAccess, t.SiteId, t.DepartmentId, ct));
            }
            default: throw new NotFoundException("Record type");
        }
    }

    async Task RequireViewAsync(Guid siteId, Guid? dept, CancellationToken ct)
    {
        if (!await access.CanAccessAsync(me.UserId!.Value, Permissions.SequflohAccess, siteId, dept, ct)) throw new NotFoundException("Record");
    }

    [HttpGet("api/records/{type}/{id:guid}/files")]
    public async Task<object> List(string type, Guid id, bool history = false, CancellationToken ct = default)
    {
        var (site, dept, _) = await OwnerAsync(type, id, ct);
        await RequireViewAsync(site, dept, ct);
        return await db.Attachments.AsNoTracking().Where(a => a.OwnerType == type && a.OwnerId == id && (history || !a.IsArchived)).OrderBy(a => a.FileName)
            .Select(a => new { a.Id, a.FileName, a.SizeBytes, a.Sha256, a.Version, a.IsLocked, a.IsArchived, a.CreatedAt, a.ContentType }).ToListAsync(ct);
    }

    [HttpPost("api/records/{type}/{id:guid}/files"), RequestSizeLimit(MaxBytes + 64 * 1024)]
    public async Task<object> Upload(string type, Guid id, IFormFile file, CancellationToken ct)
    {
        var (site, _, editable) = await OwnerAsync(type, id, ct);
        if (!editable) throw new ForbiddenException();
        var a = await AttachAsync(db, storage, type, id, site, file, ct);
        return new { a.Id, a.FileName };
    }

    [HttpGet("api/files/{id:guid}")]
    public async Task<IActionResult> Download(Guid id, bool inline = true, CancellationToken ct = default)
    {
        var a = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("File");
        var (site, dept, _) = await OwnerAsync(a.OwnerType, a.OwnerId, ct);
        await RequireViewAsync(site, dept, ct);
        var s = await storage.GetAsync(a.StorageKey, ct) ?? throw new NotFoundException("File content");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var safeInline = inline && (a.ContentType.StartsWith("image/") && a.ContentType != "image/svg+xml" || a.ContentType == "application/pdf");
        return File(s, a.ContentType, safeInline ? null : a.FileName);
    }

    [HttpPost("api/files/{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("File");
        var (_, _, editable) = await OwnerAsync(a.OwnerType, a.OwnerId, ct);
        if (!editable) throw new ForbiddenException();
        if (a.IsLocked) throw new AppException("This file belongs to a released revision and cannot be removed.");
        a.IsArchived = true; a.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
