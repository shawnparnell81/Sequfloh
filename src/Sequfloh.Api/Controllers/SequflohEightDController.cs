using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>8D reports on work orders, inspection requests and projects. Access follows the record (checked in the service).</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/8d")]
public sealed class SequflohEightDController(SequflohEightDService svc) : ControllerBase
{
    [HttpGet("{kind}/{id:guid}")] public Task<object> List(string kind, Guid id, CancellationToken ct) => svc.ListAsync(kind, id, ct);

    [HttpPost("{kind}/{id:guid}"), RequestSizeLimit(global::Sequfloh.Platform.FilesController.MaxBytes + 64 * 1024)]
    public async Task<object> Upload(string kind, Guid id, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new global::Sequfloh.Application.Abstractions.AppException("Choose a file.");
        await using var s = file.OpenReadStream();
        return await svc.UploadAsync(kind, id, file.FileName, s, ct);
    }

    [HttpPut("{kind}/{id:guid}/include")]
    public async Task<IActionResult> Include(string kind, Guid id, EightDIncludeInput r, CancellationToken ct) { await svc.SetIncludeAsync(kind, id, r.Include, ct); return NoContent(); }

    [HttpGet("files/{fileId:guid}")]
    public async Task<IActionResult> Open(Guid fileId, bool download = false, CancellationToken ct = default)
    {
        var (s, type, name) = await svc.OpenAsync(fileId, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!download && type == "application/pdf")
        {
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; object-src 'self'; frame-ancestors 'self'";
            Response.Headers["Content-Disposition"] = new System.Net.Mime.ContentDisposition { Inline = true, FileName = name }.ToString();
            return File(s, type);
        }
        return File(s, type, name);
    }

    [HttpDelete("files/{fileId:guid}")]
    public async Task<IActionResult> Remove(Guid fileId, CancellationToken ct) { await svc.RemoveAsync(fileId, ct); return NoContent(); }
}
