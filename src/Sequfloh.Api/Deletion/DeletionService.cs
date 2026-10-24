using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Common;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Deletion;

/// <summary>
/// Deletes records people added by mistake or no longer need. Every delete needs the "Delete records" permission for the record's site and department,
/// writes one audit entry, and removes the stored files that went with the record. Records that other work depends on are not deleted; the message says why
/// and what to do instead (time logged on a work order is kept for chargebacks).
/// </summary>
public sealed class DeletionService(AppDbContext db, IFileStorage storage, IAccessService access, ICurrentUser me, ILogger<DeletionService> log)
{
    Guid UserId => me.UserId ?? throw new ForbiddenException();

    async Task RequireAsync(ISiteScoped? x, CancellationToken ct)
    {
        var ok = x is null
            ? await access.HasPermissionAsync(UserId, Permissions.SequflohDeleteRecords, ct)
            : await access.CanAccessAsync(UserId, Permissions.SequflohDeleteRecords, x.SiteId, x.DepartmentId, ct);
        if (!ok) throw new ForbiddenException("You do not have permission to delete this.");
    }

    static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    async Task SaveAndRemoveFilesAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        foreach (var k in keys.Where(k => !string.IsNullOrEmpty(k)).Distinct())
        {
            try { await storage.DeleteAsync(k, ct); }
            catch (Exception e) { log.LogWarning(e, "Stored file {Key} could not be removed after its record was deleted", k); }
        }
    }

    async Task<List<string>> AttachmentKeysAsync(string ownerType, IReadOnlyCollection<Guid> owners, CancellationToken ct)
    {
        var files = await db.Attachments.Where(a => a.OwnerType == ownerType && owners.Contains(a.OwnerId)).ToListAsync(ct);
        db.Attachments.RemoveRange(files);
        return files.Select(f => f.StorageKey).ToList();
    }

    Task ForgetRecentAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        db.SequflohUserItems.Where(i => i.RecordId != null && ids.Contains(i.RecordId.Value)).ExecuteDeleteAsync(ct);

    // ---- work instructions ----------------------------------------------------------------------------------------------------------

    /// <summary>Deletes a work instruction with all its revisions. Not possible once work orders were created from it (mark it obsolete instead).</summary>
    public async Task WorkInstructionAsync(Guid id, CancellationToken ct)
    {
        var wi = await db.SequflohWorkInstructions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work instruction");
        await RequireAsync(wi, ct);
        var family = await db.SequflohWorkInstructions.Where(x => x.FamilyId == wi.FamilyId).ToListAsync(ct);
        var ids = family.Select(x => x.Id).ToList();
        var orders = await db.SequflohTrackingRecords.CountAsync(t => t.WorkInstructionId != null && ids.Contains(t.WorkInstructionId.Value), ct);
        if (orders > 0)
            throw new ConflictException($"{Count(orders, "work order was", "work orders were")} created from {wi.Number}, so it can't be deleted. Mark it obsolete instead.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var keys = await AttachmentKeysAsync(nameof(SequflohWorkInstruction), ids, ct);
        await db.SequflohWorkInstructionTransitions.Where(t => ids.Contains(t.WorkInstructionId)).ExecuteDeleteAsync(ct);
        await ForgetRecentAsync(ids, ct);
        // unlink revisions first so they can go in any order
        await db.SequflohWorkInstructions.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviousRevisionId, (Guid?)null), ct);
        foreach (var r in family) await db.Entry(r).ReloadAsync(ct);
        if (family.Count > 1) AuditInterceptor.Note(wi, $"with all {family.Count} revisions");
        db.SequflohWorkInstructions.RemoveRange(family);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await SaveAndRemoveFilesAsync(keys, ct);
    }

    // ---- work orders ----------------------------------------------------------------------------------------------------------------

    /// <summary>True when time has been logged on the work order (it counts toward chargebacks).</summary>
    public static bool HasTime(SequflohTrackingRecord t) =>
        t.ActiveSeconds + t.PausedSeconds + t.HoldSeconds > 0 || t.ActualStart is not null ||
        t.Status is TrackingStatus.InProgress or TrackingStatus.Paused or TrackingStatus.OnHold or TrackingStatus.Completed or TrackingStatus.Failed;

    /// <summary>Deletes a work order nobody has logged time on. Work orders with time stay for chargebacks; cancel them instead.</summary>
    public async Task WorkOrderAsync(Guid id, CancellationToken ct)
    {
        var t = await db.SequflohTrackingRecords.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order");
        await RequireAsync(t, ct);
        if (HasTime(t) || await db.SequflohTimeEntries.AnyAsync(e => e.TrackingRecordId == id, ct) || await db.SequflohCrewSessions.AnyAsync(e => e.TrackingRecordId == id, ct))
            throw new ConflictException($"Time has been logged on {t.Number} and is kept for chargebacks, so it can't be deleted. Cancel it instead.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var keys = await AttachmentKeysAsync(nameof(SequflohTrackingRecord), [id], ct);
        keys.AddRange(await db.SequflohTestFiles.Where(e => e.TrackingRecordId == id).Select(e => e.StorageKey).ToListAsync(ct));
        await db.SequflohTestFiles.Where(e => e.TrackingRecordId == id).ExecuteDeleteAsync(ct);
        await db.SequflohTaskEvents.Where(e => e.TrackingRecordId == id).ExecuteDeleteAsync(ct);
        await db.SequflohTaskAssignees.Where(e => e.TrackingRecordId == id).ExecuteDeleteAsync(ct);
        await ForgetRecentAsync([id], ct);
        db.SequflohTrackingRecords.Remove(t);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await SaveAndRemoveFilesAsync(keys, ct);
    }

    // ---- parts ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Deletes a part. Drawing links go with it (the drawings stay). Not possible while instructions, work orders or parts lists use it.</summary>
    public async Task PartAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await RequireAsync(p, ct);
        var wis = await db.SequflohWorkInstructions.CountAsync(w => w.PartId == id, ct);
        var orders = await db.SequflohTrackingRecords.CountAsync(w => w.PartId == id, ct);
        var lists = await db.SequflohPartsLists.CountAsync(l => l.ParentPartId == id, ct) + await db.SequflohPartsListItems.CountAsync(l => l.PartId == id, ct);
        var uses = new List<string>();
        if (wis > 0) uses.Add(Count(wis, "work instruction", "work instructions"));
        if (orders > 0) uses.Add(Count(orders, "work order", "work orders"));
        if (lists > 0) uses.Add(Count(lists, "parts list entry", "parts list entries"));
        if (uses.Count > 0)
            throw new ConflictException($"{p.PartNumber} is used by {string.Join(", ", uses)}, so it can't be deleted. Deactivate it instead.");
        var links = await db.DrawingPartLinks.CountAsync(l => l.PartId == id, ct);
        if (links > 0) AuditInterceptor.Note(p, $"removed its links to {Count(links, "drawing", "drawings")} (the drawings stay)");
        await db.PartRevisions.Where(r => r.PartId == id).ExecuteDeleteAsync(ct);
        await db.DrawingPartLinks.Where(l => l.PartId == id).ExecuteDeleteAsync(ct);
        await db.DrawingFolders.Where(f => f.PartId == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.PartId, (Guid?)null), ct);
        await ForgetRecentAsync([id], ct);
        db.Parts.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    public async Task PartsListAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(null, ct);
        var l = await db.SequflohPartsLists.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Parts list");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SequflohPartsListRevisions.Where(r => r.PartsListId == id).ExecuteDeleteAsync(ct);
        await db.SequflohWorkInstructions.Where(w => w.SourcePartsListId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.SourcePartsListId, (Guid?)null), ct);
        if (l.Items.Count > 0) AuditInterceptor.Note(l, $"with {Count(l.Items.Count, "item", "items")}");
        db.SequflohPartsListItems.RemoveRange(l.Items);
        db.SequflohPartsLists.Remove(l);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Deletes an inspection request and its attachments. A work order made from it stays.</summary>
    public async Task InspectionRequestAsync(Guid id, CancellationToken ct)
    {
        var r = await db.SequflohInspectionRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Inspection request");
        await RequireAsync(r, ct);
        if (r.WorkOrderNumber is { } wo) AuditInterceptor.Note(r, $"work order {wo} made from it stays");
        var keys = await AttachmentKeysAsync(nameof(SequflohInspectionRequest), [id], ct);
        db.SequflohInspectionRequests.Remove(r);
        await ForgetRecentAsync([id], ct);
        await SaveAndRemoveFilesAsync(keys, ct);
    }

    public async Task OperationStepAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(null, ct);
        var s = await db.SequflohOperationSteps.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Operation step");
        AuditInterceptor.Note(s, "copies already placed in instructions stay");
        db.SequflohOperationSteps.Remove(s);
        await db.SaveChangesAsync(ct);
    }

    public async Task TemplateAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(null, ct);
        var t = await db.SequflohInstructionTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template");
        await db.SequflohWorkInstructions.Where(w => w.TemplateId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.TemplateId, (Guid?)null), ct);
        db.SequflohInstructionTemplates.Remove(t);
        await db.SaveChangesAsync(ct);
    }

    // ---- drawings -------------------------------------------------------------------------------------------------------------------

    /// <summary>Deletes a drawing with every revision file. Its links to parts and work orders are removed; the parts and work orders stay.</summary>
    public async Task DrawingAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Drawings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Drawing");
        await RequireAsync(d, ct);
        var revs = await db.DrawingRevisions.Where(r => r.DrawingId == id).ToListAsync(ct);
        var parts = await db.DrawingPartLinks.CountAsync(l => l.DrawingId == id, ct);
        var orders = await db.TrackingRecordDrawings.CountAsync(l => l.DrawingId == id, ct);
        var note = new List<string> { Count(revs.Count, "revision", "revisions") };
        if (parts > 0) note.Add($"unlinked from {Count(parts, "part", "parts")}");
        if (orders > 0) note.Add($"removed from {Count(orders, "work order", "work orders")}");
        AuditInterceptor.Note(d, "with " + string.Join("; ", note));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.DrawingPartLinks.Where(l => l.DrawingId == id).ExecuteDeleteAsync(ct);
        await db.TrackingRecordDrawings.Where(l => l.DrawingId == id).ExecuteDeleteAsync(ct);
        await ForgetRecentAsync([id], ct);
        db.DrawingRevisions.RemoveRange(revs);
        db.Drawings.Remove(d);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await SaveAndRemoveFilesAsync(revs.Select(r => r.StorageKey), ct);
    }

    /// <summary>Deletes one revision of a drawing. The last revision can't go on its own (delete the drawing instead).</summary>
    public async Task DrawingRevisionAsync(Guid drawingId, string revision, CancellationToken ct)
    {
        var d = await db.Drawings.FirstOrDefaultAsync(x => x.Id == drawingId, ct) ?? throw new NotFoundException("Drawing");
        await RequireAsync(d, ct);
        var revs = await db.DrawingRevisions.Where(r => r.DrawingId == drawingId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var rev = revs.FirstOrDefault(r => r.Revision == revision) ?? throw new NotFoundException("Revision");
        if (revs.Count == 1) throw new ConflictException($"Revision {revision} is the only file of this drawing. Delete the drawing instead.");
        db.DrawingRevisions.Remove(rev);
        var current = revs.Where(r => r != rev).Last();
        d.CurrentRevision = current.Revision;
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Deleted", EntityType = nameof(Drawing), EntityId = d.Id, SiteId = d.SiteId,
            CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Deleted revision {rev.Revision} ({rev.FileName}) of {d.AuditLabel}; current revision is {current.Revision}"),
        });
        await SaveAndRemoveFilesAsync([rev.StorageKey], ct);
    }

    /// <summary>Deletes an empty drawing folder.</summary>
    public async Task DrawingFolderAsync(Guid id, CancellationToken ct)
    {
        var f = await db.DrawingFolders.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Folder");
        await RequireAsync(f, ct);
        var subs = await db.DrawingFolders.CountAsync(x => x.ParentId == id, ct);
        var drawings = await db.Drawings.CountAsync(x => x.FolderId == id, ct);
        if (subs + drawings > 0)
        {
            var what = new List<string>();
            if (drawings > 0) what.Add(Count(drawings, "drawing", "drawings"));
            if (subs > 0) what.Add(Count(subs, "folder", "folders"));
            throw new ConflictException($"{f.Name} still holds {string.Join(" and ", what)} (archived ones included). Delete those first.");
        }
        await ForgetRecentAsync([id], ct);
        db.DrawingFolders.Remove(f);
        await db.SaveChangesAsync(ct);
    }

    // ---- imported files -------------------------------------------------------------------------------------------------------------

    /// <summary>Deletes an imported file (or a filled copy) with every saved version. A blank form can't go while filled copies made from it exist.</summary>
    public async Task ImportedFileAsync(Guid id, CancellationToken ct)
    {
        var f = await db.ImportedFiles.Include(x => x.Versions).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("File");
        await RequireAsync(f, ct);
        if (f.IsBlankForm)
        {
            var copies = await db.ImportedFiles.CountAsync(x => x.BlankFormId == id, ct);
            if (copies > 0)
                throw new ConflictException($"{Count(copies, "filled copy was", "filled copies were")} made from this blank form. Delete the filled copies first, or archive the blank form to hide it.");
        }
        var keys = f.Versions.Select(v => v.StorageKey).Append(f.OriginalStorageKey).ToList();
        AuditInterceptor.Note(f, $"with {Count(f.Versions.Count, "saved version", "saved versions")}");
        await ForgetRecentAsync([id], ct);
        db.ImportedFileVersions.RemoveRange(f.Versions);
        db.ImportedFiles.Remove(f);
        await SaveAndRemoveFilesAsync(keys, ct);
    }

    // ---- sites and departments ------------------------------------------------------------------------------------------------------

    async Task<List<string>> SiteUsesAsync(Guid? site, Guid? dept, CancellationToken ct)
    {
        var uses = new List<string>();
        async Task Add(Task<int> n, string one, string many) { var c = await n; if (c > 0) uses.Add(Count(c, one, many)); }
        if (dept is { } d)
        {
            await Add(db.SequflohWorkInstructions.CountAsync(x => x.DepartmentId == d, ct), "work instruction", "work instructions");
            await Add(db.SequflohTrackingRecords.CountAsync(x => x.DepartmentId == d, ct), "work order", "work orders");
            await Add(db.Parts.CountAsync(x => x.DepartmentId == d, ct), "part", "parts");
            await Add(db.Drawings.CountAsync(x => x.DepartmentId == d, ct), "drawing", "drawings");
            await Add(db.DrawingFolders.CountAsync(x => x.DepartmentId == d, ct), "drawing folder", "drawing folders");
            await Add(db.ImportedFiles.CountAsync(x => x.DepartmentId == d, ct), "imported file", "imported files");
            await Add(db.UserRoleAssignments.CountAsync(x => x.DepartmentId == d, ct), "role assignment", "role assignments");
        }
        else if (site is { } st)
        {
            await Add(db.Departments.CountAsync(x => x.SiteId == st, ct), "department", "departments");
            await Add(db.SequflohWorkInstructions.CountAsync(x => x.SiteId == st, ct), "work instruction", "work instructions");
            await Add(db.SequflohTrackingRecords.CountAsync(x => x.SiteId == st, ct), "work order", "work orders");
            await Add(db.Parts.CountAsync(x => x.SiteId == st, ct), "part", "parts");
            await Add(db.Drawings.CountAsync(x => x.SiteId == st, ct), "drawing", "drawings");
            await Add(db.DrawingFolders.CountAsync(x => x.SiteId == st, ct), "drawing folder", "drawing folders");
            await Add(db.ImportedFiles.CountAsync(x => x.SiteId == st, ct), "imported file", "imported files");
            await Add(db.UserRoleAssignments.CountAsync(x => x.SiteId == st, ct), "role assignment", "role assignments");
            await Add(db.Users.CountAsync(x => x.DefaultSiteId == st, ct), "person's home site", "people's home site");
        }
        return uses;
    }

    async Task SaveOrInUseAsync(string what, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23503" })
        { throw new ConflictException($"{what} is still used by other records, so it can't be deleted. Archive it instead."); }
    }

    /// <summary>Deletes a site nothing uses yet (set up by mistake). Sites in use are archived instead.</summary>
    public async Task SiteAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Sites.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Site");
        var uses = await SiteUsesAsync(id, null, ct);
        if (uses.Count > 0) throw new ConflictException($"Site {s.Code} has {string.Join(", ", uses)}, so it can't be deleted. Archive it instead.");
        db.Sites.Remove(s);
        await SaveOrInUseAsync($"Site {s.Code}", ct);
    }

    /// <summary>Deletes a department nothing uses yet. Departments in use are archived instead.</summary>
    public async Task DepartmentAsync(Guid id, CancellationToken ct)
    {
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Department");
        var uses = await SiteUsesAsync(null, id, ct);
        if (uses.Count > 0) throw new ConflictException($"Department {d.Code} has {string.Join(", ", uses)}, so it can't be deleted. Archive it instead.");
        db.Departments.Remove(d);
        await SaveOrInUseAsync($"Department {d.Code}", ct);
    }
}
