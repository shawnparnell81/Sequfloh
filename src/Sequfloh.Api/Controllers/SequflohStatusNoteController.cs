using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>
/// Informational status notes on parts, work instructions and work orders (for example "Quarantine hold (NetSuite)").
/// A note never blocks work. Whoever can edit the record can set it; work orders can be noted by anyone working them. Changes are audited.
/// </summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/status-note")]
public sealed class SequflohStatusNoteController(AppDbContext db, ModuleSupport m, SequflohFoundationService f) : ControllerBase
{
    public sealed record NoteRequest(string? Tag, string? Note);

    [HttpPut("{kind}/{id:guid}")]
    public async Task<object> Set(string kind, Guid id, NoteRequest r, CancellationToken ct)
    {
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note?.Length > 300) throw new AppException("A status note can be at most 300 characters.");
        var tag = string.IsNullOrWhiteSpace(r.Tag) ? null : r.Tag.Trim();
        if (tag is not null) await f.RequireOptionAsync("status-tags", tag, "Official status in NetSuite", ct);
        switch (kind)
        {
            case "part":
                var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
                await m.RequireAsync(Permissions.SequflohEditParts, p.SiteId, p.DepartmentId, ct);
                p.StatusTag = tag; p.StatusNote = note; break;
            case "instruction":
                var w = await db.SequflohWorkInstructions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work instruction");
                await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, w.SiteId, w.DepartmentId, ct);
                w.StatusTag = tag; w.StatusNote = note; break;
            case "work-order":
                var t = await db.SequflohTrackingRecords.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order");
                await m.RequireAsync(Permissions.SequflohAccess, t.SiteId, t.DepartmentId, ct);
                t.StatusTag = tag; t.StatusNote = note; break;
            default: throw new NotFoundException("Record type");
        }
        await db.SaveChangesAsync(ct); // the audit trail records the change
        return new { StatusTag = tag, StatusNote = note };
    }
}
