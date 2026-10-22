using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Records;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// SEQUFLOH notifications on the shared notification service: assigned work, failed records,
/// released and superseded instructions. Each kind can be turned off in the SEQUFLOH notification settings, and a person
/// is notified only while they still have SEQUFLOH access to the record's site and department. Overdue and approaching-due reminders come from the daily
/// reminder job.
/// </summary>
public sealed class SequflohNotifications(INotifier notifier, IAccessService access, ICurrentUser current, SequflohFoundationService f)
{
    public const string WiType = SequflohInstructionService.EntityType;
    public const string TrackingType = SequflohTrackingService.EntityType;

    public static RecordInfo Info(SequflohWorkInstruction x) => new(WiType, x.Id, x.SiteId, x.DepartmentId, $"{x.Number} rev {x.Revision}", x.Title, x.Status.ToString(),
        Permissions.SequflohAccess, Permissions.SequflohEditDraftWorkInstruction, $"/sequfloh/instruction/{x.Id}", x.Status is not WorkInstructionStatus.Draft);
    public static RecordInfo Info(SequflohTrackingRecord x) => new(TrackingType, x.Id, x.SiteId, x.DepartmentId, x.Number, $"{x.WorkInstructionNumber} rev {x.WorkInstructionRevision} {x.WorkInstructionTitle}".Trim(),
        x.Status.ToString(), Permissions.SequflohAccess, Permissions.SequflohAccess, $"/sequfloh/record/{x.Id}", x.Status is TrackingStatus.Completed or TrackingStatus.Canceled);

    async Task SendAsync(IEnumerable<Guid?> users, string title, RecordInfo info, CancellationToken ct)
    {
        var list = new List<Guid>();
        foreach (var u in users.Where(u => u is not null && u != current.UserId).Select(u => u!.Value).Distinct())
            if (await access.CanAccessAsync(u, Permissions.SequflohAccess, info.SiteId, info.DepartmentId, ct)) list.Add(u);
        if (list.Count > 0) await notifier.NotifyAsync(list, title, null, info, email: true, ct);
    }
    async Task<IReadOnlyList<Guid>> HoldersAsync(string permission, Guid site, Guid? dept, CancellationToken ct) =>
        await access.UsersWithPermissionAsync(permission, site, dept, ct);

    public async Task InstructionChangedAsync(SequflohWorkInstruction x, string action, IReadOnlyList<SequflohWorkInstruction> superseded, CancellationToken ct)
    {
        var n = (await f.GetSettingsAsync(ct)).Notifications;
        var info = Info(x);
        var label = $"{x.Number} rev {x.Revision}";
        switch (action)
        {
            case "release" when n.ReleasedInstructions:
                await SendAsync([x.AuthorId, x.OwnerId, x.AssignedEngineerId], $"Work instruction {label} was released: {x.Title}", info, ct);
                break;
        }
        if (n.SupersededInstructions)
            foreach (var old in superseded.Concat(action == "supersede" ? [x] : []))
                await SendAsync([old.AuthorId, old.OwnerId], $"Work instruction {old.Number} rev {old.Revision} was superseded{(old.Id == x.Id ? "" : $" by rev {x.Revision}")}", Info(old), ct);
    }

    public async Task TrackingChangedAsync(SequflohTrackingRecord x, string action, Guid? assignedBefore, CancellationToken ct)
    {
        var n = (await f.GetSettingsAsync(ct)).Notifications;
        var info = Info(x);
        if (n.AssignedWork && x.AssignedUserId is { } a && a != assignedBefore)
            await SendAsync([a], $"{x.Number} was assigned to you ({x.WorkInstructionNumber} rev {x.WorkInstructionRevision}{(x.Operation is null ? "" : ", " + x.Operation)})", info, ct);
        if (n.FailedRecords && x.Status == TrackingStatus.Failed)
            await SendAsync([x.OwnerId, x.AssignedUserId, .. (await HoldersAsync(Permissions.SequflohEditQualityFolder, x.SiteId, x.DepartmentId, ct)).Cast<Guid?>()], $"{x.Number} failed and needs attention", info, ct);
    }
}
