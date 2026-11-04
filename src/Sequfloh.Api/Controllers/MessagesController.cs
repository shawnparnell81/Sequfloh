using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Messages;

namespace Sequfloh.Api.Controllers;

public sealed record NewConversationRequest(List<Guid>? RecipientIds, string? Title, string? Body, string? LinkKind, Guid? LinkId);
public sealed record NewMessageRequest(string? Body, string? LinkKind, Guid? LinkId);
public sealed record EditMessageRequest(string? Body);

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

    [HttpPost("conversations/{id:guid}/messages"), RequirePermission(Permissions.MessagesUse)]
    public Task<object> Reply(Guid id, NewMessageRequest r, CancellationToken ct) =>
        messages.ReplyAsync(id, r.Body, new MessageLinkInput(r.LinkKind, r.LinkId), ct);

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
