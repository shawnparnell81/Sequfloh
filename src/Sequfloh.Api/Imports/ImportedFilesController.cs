using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;

namespace Sequfloh.Imports;

[ApiController, Route("api/imported-files")]
public sealed class ImportedFilesController(ImportedFileService files) : ControllerBase
{
    public sealed record SaveCellsRequest(int BaseVersion, List<CellEdit> Edits, bool EditBlank = false);
    public sealed record FillOutRequest(string? Title);
    public sealed record SaveCopyRequest(int BaseVersion, string? Title, List<CellEdit>? Cells, List<ParagraphEdit>? Paragraphs);
    public sealed record PreviewCellsRequest(string? Sheet, List<CellEdit> Edits);
    public sealed record SaveDocumentRequest(int BaseVersion, List<ParagraphEdit> Edits, bool EditBlank = false);
    public sealed record RenameRequest(string Title);

    [HttpGet, RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> List(string? q, string? kind, int page = 1, int pageSize = 50, CancellationToken ct = default) => files.ListAsync(q, kind, page, pageSize, ct);

    [HttpPost, RequirePermission(Permissions.SequflohImportFiles), RequestSizeLimit(ImportedFileService.MaxBytes + 64 * 1024)]
    public async Task<object> Upload(IFormFile file, [FromForm] string? title, [FromForm] Guid? siteId, [FromForm] Guid? departmentId, [FromForm] bool blankForm, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Application.Abstractions.AppException("Choose a file.");
        await using var s = file.OpenReadStream();
        var f = await files.UploadAsync(s, file.FileName, title, siteId, departmentId, ct, blankForm);
        return new { f.Id, f.Number, f.Title };
    }

    [HttpGet("{id:guid}"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> Get(Guid id, CancellationToken ct) => files.GetAsync(id, ct);

    [HttpGet("{id:guid}/sheet"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> Sheet(Guid id, string? name, int? version, CancellationToken ct) => files.SheetAsync(id, name, version, ct);

    [HttpPut("{id:guid}/cells"), RequirePermission(Permissions.SequflohImportFiles)]
    public Task<object> SaveCells(Guid id, SaveCellsRequest r, CancellationToken ct) => files.SaveCellsAsync(id, r.BaseVersion, r.Edits ?? [], ct, r.EditBlank);

    [HttpPost("{id:guid}/preview"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> Preview(Guid id, PreviewCellsRequest r, CancellationToken ct) => files.PreviewAsync(id, r.Sheet ?? "", r.Edits ?? [], ct);

    [HttpGet("{id:guid}/document"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> Document(Guid id, int? version, CancellationToken ct) => files.DocumentAsync(id, version, ct);

    [HttpPut("{id:guid}/document"), RequirePermission(Permissions.SequflohImportFiles)]
    public Task<object> SaveDocument(Guid id, SaveDocumentRequest r, CancellationToken ct) => files.SaveDocumentAsync(id, r.BaseVersion, r.Edits ?? [], ct, r.EditBlank);

    [HttpGet("{id:guid}/download"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public async Task<IActionResult> Download(Guid id, int? version, bool original = false, CancellationToken ct = default)
    {
        var (content, type, name) = await files.DownloadAsync(id, version, original, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(content, type, name);
    }

    [HttpGet("{id:guid}/history"), RequirePermission(Permissions.SequflohViewImportedFiles)]
    public Task<object> History(Guid id, CancellationToken ct) => files.HistoryAsync(id, ct);

    [HttpPut("{id:guid}/title"), RequirePermission(Permissions.SequflohImportFiles)]
    public async Task<IActionResult> Rename(Guid id, RenameRequest r, CancellationToken ct) { await files.RenameAsync(id, r.Title, ct); return NoContent(); }

    [HttpPost("{id:guid}/archive"), RequirePermission(Permissions.SequflohImportFiles)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) { await files.ArchiveAsync(id, ct); return NoContent(); }

    [HttpPost("{id:guid}/fill-out"), RequirePermission(Permissions.SequflohImportFiles)]
    public Task<object> FillOut(Guid id, FillOutRequest r, CancellationToken ct) => files.FillOutAsync(id, r.Title, ct);

    [HttpPost("{id:guid}/save-as-copy"), RequirePermission(Permissions.SequflohImportFiles)]
    public Task<object> SaveAsCopy(Guid id, SaveCopyRequest r, CancellationToken ct) => files.SaveAsCopyAsync(id, r.BaseVersion, r.Title, r.Cells, r.Paragraphs, ct);

    [HttpPost("{id:guid}/versions"), RequirePermission(Permissions.SequflohImportFiles), RequestSizeLimit(ImportedFileService.MaxBytes + 64 * 1024)]
    public async Task<object> UploadVersion(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new Application.Abstractions.AppException("Choose a file.");
        await using var s = file.OpenReadStream();
        return await files.UploadVersionAsync(id, s, file.FileName, ct);
    }
}
