using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Messages;

namespace Sequfloh.Api.Controllers;

public sealed record NewConversationRequest(List<Guid>? RecipientIds, string? Title, string? Body, string? LinkKind, Guid? LinkId);
public sealed record NewMessageRequest(string? Body, string? LinkKind, Guid? LinkId);
public sealed record EditMessageRequest(string? Body);
public sealed record ReactionRequest(string? Emoji);

/// <summary>Messages between people in the app: one-to-one threads and named groups. Opening a thread marks it read for that person.</summary>
[ApiController, Authorize, Route("api/messages")]
public sealed class MessagesController(MessageService messages) : ControllerBase
{
    [HttpGet("unread-count"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Unread(CancellationToken ct) => messages.UnreadCountAsync(ct);

    [HttpGet("people"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> People([FromQuery] string? q, CancellationToken ct) => messages.PeopleAsync(q, ct);

    [HttpGet("records"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Records([FromQuery] string? kind, [FromQuery] string? q, CancellationToken ct) => messages.RecordsAsync(kind, q, ct);

    [HttpGet("conversations"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> List(CancellationToken ct) => messages.ListAsync(ct);

    [HttpGet("conversations/{id:guid}"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Get(Guid id, CancellationToken ct) => messages.GetAsync(id, ct);

    [HttpPost("conversations"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Start(NewConversationRequest r, CancellationToken ct) =>
        messages.StartAsync(r.RecipientIds, r.Title, r.Body, new MessageLinkInput(r.LinkKind, r.LinkId), ct);

    const long UploadLimit = MessageService.MaxFileBytes * MessageService.MaxFiles + 256 * 1024;

    [HttpPost("conversations/with-files"), RequirePermission(Permissions.MessagesUse), RequestSizeLimit(UploadLimit), RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    public async Task<object> StartFiles([FromForm] List<Guid>? recipientIds, [FromForm] string? title, [FromForm] string? body, [FromForm] string? linkKind, [FromForm] Guid? linkId, [FromForm] List<IFormFile>? files, CancellationToken ct) =>
        await messages.StartAsync(recipientIds, title, body, new MessageLinkInput(linkKind, linkId), ct, await ReadFilesAsync(files, ct));

    [HttpPost("conversations/{id:guid}/messages"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Reply(Guid id, NewMessageRequest r, CancellationToken ct) =>
        messages.ReplyAsync(id, r.Body, new MessageLinkInput(r.LinkKind, r.LinkId), ct);

    [HttpPost("conversations/{id:guid}/messages/with-files"), RequirePermission(Permissions.MessagesUse), RequestSizeLimit(UploadLimit), RequestFormLimits(MultipartBodyLengthLimit = UploadLimit)]
    public async Task<object> ReplyFiles(Guid id, [FromForm] string? body, [FromForm] string? linkKind, [FromForm] Guid? linkId, [FromForm] List<IFormFile>? files, CancellationToken ct) =>
        await messages.ReplyAsync(id, body, new MessageLinkInput(linkKind, linkId), ct, await ReadFilesAsync(files, ct));

    [HttpPost("messages/{id:guid}/reactions"), RequirePermission(Permissions.MessagesUse)]
    public async Task<IActionResult> React(Guid id, ReactionRequest r, CancellationToken ct)
    {
        await messages.ToggleReactionAsync(id, r.Emoji, ct);
        return NoContent();
    }

    /// <summary>A file on a message. Conversation members can read it, and so can someone reviewing conversations. Anyone else is told it is not there.</summary>
    [HttpGet("files/{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var (stream, type, name) = await messages.OpenFileAsync(id, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var image = type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        if (image)
        {
            Response.Headers.ContentDisposition = new ContentDisposition { Inline = true, FileName = name }.ToString();
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
            return File(stream, type);
        }
        return File(stream, type, name);
    }

    static async Task<List<IncomingFile>> ReadFilesAsync(List<IFormFile>? files, CancellationToken ct)
    {
        var list = new List<IncomingFile>();
        foreach (var file in files ?? [])
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            list.Add(new IncomingFile(file.FileName, file.ContentType, ms.ToArray()));
        }
        return list;
    }

    [HttpPost("conversations/{id:guid}/read"), RequirePermission(Permissions.MessagesUse)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        await messages.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPut("messages/{id:guid}"), RequirePermission(Permissions.MessagesUse)]
    public async Task<IActionResult> Edit(Guid id, EditMessageRequest r, CancellationToken ct)
    {
        await messages.EditAsync(id, r.Body, ct);
        return NoContent();
    }

    [HttpDelete("messages/{id:guid}"), RequirePermission(Permissions.MessagesUse)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await messages.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("admin/conversations"), RequirePermission(Permissions.MessagesAdmin)]
    public Task<object> All(CancellationToken ct) => messages.AdminListAsync(ct);

    [HttpGet("admin/conversations/{id:guid}"), RequirePermission(Permissions.MessagesAdmin)]
    public Task<object> AdminGet(Guid id, CancellationToken ct) => messages.AdminGetAsync(id, ct);
}
