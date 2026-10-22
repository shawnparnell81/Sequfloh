using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;

namespace Sequfloh.Drawings;

[ApiController, Route("api/drawings")]
public sealed class DrawingsController(DrawingService drawings) : ControllerBase
{
    public sealed record ArchiveRequest(bool Archived = true);
    public sealed record LinksRequest(List<Guid>? Ids);
    const long Limit = DrawingService.MaxBytes + 64 * 1024;

    [HttpGet("folders"), RequirePermission(Permissions.SequflohViewDrawings)]
    public Task<object> Folders(Guid? parentId, bool archived = false, CancellationToken ct = default) => drawings.FoldersAsync(parentId, archived, ct);

    [HttpPost("folders"), RequirePermission(Permissions.SequflohManageDrawings)]
    public Task<object> CreateFolder(DrawingService.FolderRequest r, CancellationToken ct) => drawings.CreateFolderAsync(r, ct);

    [HttpPut("folders/{id:guid}"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> UpdateFolder(Guid id, DrawingService.FolderRequest r, CancellationToken ct) { await drawings.UpdateFolderAsync(id, r, ct); return NoContent(); }

    [HttpPost("folders/{id:guid}/archive"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> ArchiveFolder(Guid id, ArchiveRequest r, CancellationToken ct) { await drawings.ArchiveFolderAsync(id, r.Archived, ct); return NoContent(); }

    [HttpGet("folders/{id:guid}/history"), RequirePermission(Permissions.SequflohViewDrawings)]
    public Task<object> FolderHistory(Guid id, CancellationToken ct) => drawings.HistoryAsync(id, true, ct);

    [HttpPost("folders/{id:guid}/drawings"), RequirePermission(Permissions.SequflohManageDrawings), RequestSizeLimit(Limit), RequestFormLimits(MultipartBodyLengthLimit = Limit)]
    public async Task<object> Upload(Guid id, IFormFile file, [FromForm] string? title, [FromForm] string? drawingNumber, [FromForm] string? revision, [FromForm] string? notes, [FromForm] string? partIds, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Application.Abstractions.AppException("Choose a file.");
        var parts = (partIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : throw new Application.Abstractions.AppException("A part to link was not recognised.")).ToList();
        await using var s = file.OpenReadStream();
        var created = await drawings.UploadAsync(id, s, file.FileName, new(title, drawingNumber, revision, notes), ct);
        if (parts.Count > 0) await drawings.SetDrawingPartsAsync((Guid)created.GetType().GetProperty("Id")!.GetValue(created)!, parts, ct);
        return created;
    }

    [HttpGet("search"), RequirePermission(Permissions.SequflohViewDrawings)]
    public Task<object> Search(string? q, CancellationToken ct) => drawings.SearchAsync(q, ct);

    [HttpGet("{id:guid}"), RequirePermission(Permissions.SequflohViewDrawings)]
    public Task<object> Get(Guid id, CancellationToken ct) => drawings.GetAsync(id, ct);

    [HttpPut("{id:guid}"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> Update(Guid id, DrawingService.DrawingFields r, CancellationToken ct) { await drawings.UpdateAsync(id, r, ct); return NoContent(); }

    [HttpPost("{id:guid}/archive"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> Archive(Guid id, ArchiveRequest r, CancellationToken ct) { await drawings.ArchiveAsync(id, r.Archived, ct); return NoContent(); }

    [HttpPost("{id:guid}/revisions"), RequirePermission(Permissions.SequflohManageDrawings), RequestSizeLimit(Limit), RequestFormLimits(MultipartBodyLengthLimit = Limit)]
    public async Task<object> UploadRevision(Guid id, IFormFile file, [FromForm] string? revision, [FromForm] string? notes, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Application.Abstractions.AppException("Choose a file.");
        await using var s = file.OpenReadStream();
        return await drawings.UploadRevisionAsync(id, s, file.FileName, revision, notes, ct);
    }

    /// <summary>The file of a revision (the current one by default). PDF and images open in the browser when view=true; everything else downloads.</summary>
    [HttpGet("{id:guid}/file"), RequirePermission(Permissions.SequflohViewDrawings)]
    public async Task<IActionResult> File(Guid id, string? revision, bool view = false, CancellationToken ct = default)
    {
        var (content, type, name, viewable) = await drawings.FileAsync(id, revision, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (view && viewable)
        {
            Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = name }.ToString();
            // Images are fully sandboxed; a sandboxed PDF would not open in the browser's own viewer, so PDFs only get the no-scripts policy.
            Response.Headers["Content-Security-Policy"] = type == "application/pdf" ? "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; object-src 'self'; frame-ancestors 'self'"
                : "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'; sandbox";
            // shown inside the app's own drawing viewer page
            Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            return File(content, type);
        }
        return File(content, viewable ? type : "application/octet-stream", name);
    }

    [HttpGet("{id:guid}/history"), RequirePermission(Permissions.SequflohViewDrawings)]
    public Task<object> History(Guid id, CancellationToken ct) => drawings.HistoryAsync(id, false, ct);

    /// <summary>Current drawings for a part, by id or by part number as typed. Anyone in job tracking may ask; without drawing access the list is empty.</summary>
    [HttpGet("for-part"), RequirePermission(Permissions.SequflohAccess)]
    public Task<List<object>> ForPart(Guid? partId, string? partNumber, CancellationToken ct) => drawings.ForPartAsync(partId, partNumber, ct);

    [HttpPut("for-part/{partId:guid}"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> SetPartDrawings(Guid partId, LinksRequest r, CancellationToken ct) { await drawings.SetPartDrawingsAsync(partId, r.Ids ?? [], ct); return NoContent(); }

    [HttpPost("for-part/{partId:guid}/upload"), RequirePermission(Permissions.SequflohManageDrawings), RequestSizeLimit(Limit), RequestFormLimits(MultipartBodyLengthLimit = Limit)]
    public async Task<object> UploadForPart(Guid partId, IFormFile file, [FromForm] string? title, [FromForm] string? drawingNumber, [FromForm] string? revision, [FromForm] string? notes, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Application.Abstractions.AppException("Choose a file.");
        await using var s = file.OpenReadStream();
        return await drawings.UploadForPartAsync(partId, s, file.FileName, new(title, drawingNumber, revision, notes), ct);
    }

    /// <summary>Drawings attached to a work order (tracking record).</summary>
    [HttpGet("for-record/{recordId:guid}"), RequirePermission(Permissions.SequflohAccess)]
    public Task<List<object>> ForRecord(Guid recordId, CancellationToken ct) => drawings.ForRecordAsync(recordId, ct);

    [HttpPut("{id:guid}/parts"), RequirePermission(Permissions.SequflohManageDrawings)]
    public async Task<IActionResult> SetDrawingParts(Guid id, LinksRequest r, CancellationToken ct) { await drawings.SetDrawingPartsAsync(id, r.Ids ?? [], ct); return NoContent(); }
}
