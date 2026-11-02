using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

public sealed record UserNoteInput(string? Text, bool? IsDone = null, DateOnly? RemindOn = null, bool ClearRemindOn = false, Guid? TrackingRecordId = null, bool ClearLink = false,
    bool? Pinned = null, string? Priority = null);

/// <summary>
/// My Notes and Reminders: a person's private notes on the My Work page. Every query is limited to the signed-in person's own notes, so nobody else's
/// note can be read, changed or deleted (another person's note answers 404). A linked work order must be one the person can open.
/// </summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/my-notes")]
public sealed class SequflohNotesController(AppDbContext db, ModuleSupport m, SequflohTrackingService tracking) : ControllerBase
{
    static readonly string[] Priorities = ["Normal", "Important"];

    IQueryable<SequflohUserNote> Mine() { var me = m.UserId; return db.SequflohUserNotes.Where(n => n.UserId == me); }

    [HttpGet]
    public async Task<object> List(CancellationToken ct)
    {
        var tr = await tracking.QueryAsync(ct);
        return await Mine().AsNoTracking().OrderByDescending(n => n.Pinned).ThenBy(n => n.RemindOn == null).ThenBy(n => n.RemindOn).ThenByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.Id, n.Text, n.IsDone, n.DoneAt, n.RemindOn, n.Pinned, n.Priority, n.CreatedAt, n.UpdatedAt, n.TrackingRecordId,
                // the work order chip only shows while the person can still open it
                Link = tr.Where(r => r.Id == n.TrackingRecordId).Select(r => new { r.Id, r.Number, r.WorkOrder }).FirstOrDefault(),
            }).ToListAsync(ct);
    }

    [HttpPost]
    public async Task<object> Add(UserNoteInput r, CancellationToken ct)
    {
        var n = new SequflohUserNote { UserId = m.UserId };
        await ApplyAsync(n, r, true, ct);
        db.SequflohUserNotes.Add(n);
        await db.SaveChangesAsync(ct);
        return new { n.Id };
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UserNoteInput r, CancellationToken ct)
    {
        var n = await Mine().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (n is null) return NotFound();
        await ApplyAsync(n, r, false, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => await Mine().Where(x => x.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();

    async Task ApplyAsync(SequflohUserNote n, UserNoteInput r, bool isNew, CancellationToken ct)
    {
        if (r.Text is not null || isNew)
        {
            var text = r.Text?.Trim() ?? "";
            if (text.Length == 0) throw new AppException("Type the note.");
            if (text.Length > 1000) throw new AppException("A note can be up to 1000 characters.");
            n.Text = text;
        }
        if (r.IsDone is { } done && done != n.IsDone) { n.IsDone = done; n.DoneAt = done ? DateTimeOffset.UtcNow : null; }
        if (r.ClearRemindOn) n.RemindOn = null; else if (r.RemindOn is { } d) n.RemindOn = d;
        if (r.ClearLink) n.TrackingRecordId = null;
        else if (r.TrackingRecordId is { } wo)
        {
            if (!await (await tracking.QueryAsync(ct)).AnyAsync(x => x.Id == wo, ct)) throw new AppException("That work order can't be linked.");
            n.TrackingRecordId = wo;
        }
        if (r.Pinned is { } p) n.Pinned = p;
        if (r.Priority is { } pr) n.Priority = Priorities.FirstOrDefault(x => x.Equals(pr, StringComparison.OrdinalIgnoreCase)) ?? throw new AppException("Priority is Normal or Important.");
    }
}
