using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Infrastructure.Modules.Quality;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Record numbers are the company's own: typed by the user (or, for an imported file, the file name as it was saved) and never made up by the app.
/// The only rule is that two records of the same type can't share a number (compared without regard to case); the format is the user's.
/// </summary>
public static class RecordNumbers
{
    public static string Clean(string? value, int max, string what = "number")
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0) throw new AppException($"Enter the {what}.");
        if (v.Length > max) throw new AppException($"The {what} can be at most {max} characters.");
        return v;
    }

    /// <summary>Blocks a number another record of the same type already uses.</summary>
    public static async Task EnsureFreeAsync(IQueryable<string> taken, string number, string label, CancellationToken ct)
    {
        var lower = number.ToLower();
        if (await taken.AnyAsync(n => n.ToLower() == lower, ct)) throw new AppException($"{label} {number} already exists. Enter a different number.");
    }
}

public sealed record RecordNumberInput(string? Number, string? Title);

/// <summary>Changes the number and title of any record or file, checked against the permission that edits that record and recorded in the audit trail with the old and new values.</summary>
public sealed class RecordNumberService(AppDbContext db, ModuleSupport m, IAccessService access, ICurrentUser me)
{
    public static readonly string[] Kinds = ["file", "work-order", "inspection-request", "work-instruction", "parts-list", "operation-step"];

    public async Task<object> ChangeAsync(string kind, Guid id, RecordNumberInput r, CancellationToken ct)
    {
        var result = await ApplyAsync(kind, id, r, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    async Task<object> ApplyAsync(string kind, Guid id, RecordNumberInput r, CancellationToken ct)
    {
        string? title = r.Title is null ? null : r.Title.Trim();
        switch (kind)
        {
            case "file":
            {
                var x = await db.ImportedFiles.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException("File");
                await m.RequireAsync(Permissions.SequflohImportFiles, x.SiteId, x.DepartmentId, ct);
                var n = RecordNumbers.Clean(r.Number, 300);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase)) await RecordNumbers.EnsureFreeAsync(db.ImportedFiles.Where(o => o.Id != id).Select(o => o.Number), n, "A file numbered", ct);
                var t = title is null ? x.Title : RecordNumbers.Clean(title, 300, "title");
                return Apply(nameof(Domain.Imports.ImportedFile), id, x.SiteId, "file", x.Number, x.Title, n, t, (a, b) => { x.Number = a; x.Title = b; });
            }
            case "work-order":
            {
                var x = await db.SequflohTrackingRecords.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Work order");
                if (!await m.CanAsync(SequflohTrackingService.CreatePermission(x.Type), x.SiteId, x.DepartmentId, ct) && !await m.CanAsync(Permissions.SequflohDispatchTask, x.SiteId, x.DepartmentId, ct))
                    throw new ForbiddenException();
                var n = RecordNumbers.Clean(r.Number, 40);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase)) await RecordNumbers.EnsureFreeAsync(db.SequflohTrackingRecords.IgnoreQueryFilters().Where(o => o.Id != id).Select(o => o.Number), n, "A work order numbered", ct);
                return Apply(SequflohTrackingService.EntityType, id, x.SiteId, "work order", x.Number, null, n, null, (a, _) => x.Number = a);
            }
            case "inspection-request":
            {
                var x = await db.SequflohInspectionRequests.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Inspection request");
                await m.RequireAsync(Permissions.SequflohAccess, x.SiteId, x.DepartmentId, ct);
                var n = RecordNumbers.Clean(r.Number, 40);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase)) await RecordNumbers.EnsureFreeAsync(db.SequflohInspectionRequests.Where(o => o.Id != id).Select(o => o.Number), n, "An inspection request numbered", ct);
                return Apply(SequflohInspectionService.EntityType, id, x.SiteId, "inspection request", x.Number, null, n, null, (a, _) => x.Number = a);
            }
            case "work-instruction":
            {
                var x = await db.SequflohWorkInstructions.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Work instruction");
                await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, x.SiteId, x.DepartmentId, ct);
                var n = RecordNumbers.Clean(r.Number, 200);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase))
                    await RecordNumbers.EnsureFreeAsync(db.SequflohWorkInstructions.IgnoreQueryFilters().Where(o => o.FamilyId != x.FamilyId).Select(o => o.Number), n, "A work instruction numbered", ct);
                var t = title is null ? x.Title : RecordNumbers.Clean(title, 300, "title");
                // every revision of the instruction carries the same number
                var revisions = await db.SequflohWorkInstructions.IgnoreQueryFilters().Where(o => o.FamilyId == x.FamilyId && o.Id != x.Id).ToListAsync(ct);
                return Apply(SequflohInstructionService.EntityType, id, x.SiteId, "work instruction", x.Number, x.Title, n, t, (a, b) => { x.Number = a; x.Title = b; foreach (var v in revisions) v.Number = a; });
            }
            case "parts-list":
            {
                if (!await access.HasPermissionAsync(m.UserId, Permissions.SequflohManagePartsLists, ct)) throw new ForbiddenException();
                var x = await db.SequflohPartsLists.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Parts list");
                var n = RecordNumbers.Clean(r.Number, 40);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase)) await RecordNumbers.EnsureFreeAsync(db.SequflohPartsLists.Where(o => o.Id != id).Select(o => o.Number), n, "A parts list numbered", ct);
                var t = title is null ? x.Title : RecordNumbers.Clean(title, 300, "title");
                return Apply(nameof(Domain.Execution.SequflohPartsList), id, null, "parts list", x.Number, x.Title, n, t, (a, b) => { x.Number = a; x.Title = b; });
            }
            case "operation-step":
            {
                if (!await access.HasPermissionAsync(m.UserId, Permissions.SequflohManageOperationSteps, ct)) throw new ForbiddenException();
                var x = await db.SequflohOperationSteps.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Operation step");
                var n = RecordNumbers.Clean(r.Number, 40);
                if (!string.Equals(n, x.Number, StringComparison.OrdinalIgnoreCase)) await RecordNumbers.EnsureFreeAsync(db.SequflohOperationSteps.Where(o => o.Id != id).Select(o => o.Number), n, "An operation step numbered", ct);
                var t = title is null ? x.Title : RecordNumbers.Clean(title, 300, "title");
                return Apply(nameof(Domain.Execution.SequflohOperationStep), id, null, "operation step", x.Number, x.Title, n, t, (a, b) => { x.Number = a; x.Title = b; });
            }
            default: throw new NotFoundException("Record type");
        }
    }

    object Apply(string entityType, Guid id, Guid? siteId, string what, string oldNumber, string? oldTitle, string number, string? title, Action<string, string?> set)
    {
        var parts = new List<string>();
        if (number != oldNumber) parts.Add($"number from \"{oldNumber}\" to \"{number}\"");
        if (title is not null && title != oldTitle) parts.Add($"title from \"{oldTitle}\" to \"{title}\"");
        if (parts.Count == 0) return new { Number = oldNumber, Title = oldTitle, Changed = false };
        set(number, title);
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Changed number", EntityType = entityType, EntityId = id, SiteId = siteId,
            CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Changed the {what} {string.Join(" and ", parts)}"),
            Changes = System.Text.Json.JsonSerializer.Serialize(new { before = new { number = oldNumber, title = oldTitle }, after = new { number, title = title ?? oldTitle } }),
        });
        return new { Number = number, Title = title ?? oldTitle, Changed = true };
    }
}
