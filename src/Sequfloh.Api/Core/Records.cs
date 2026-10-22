namespace Sequfloh.Application.Records;

/// <summary>What notifications need to know about a record.</summary>
public sealed record RecordInfo(
    string Type, Guid Id, Guid SiteId, Guid? DepartmentId, string Number, string Title, string? Status,
    string ViewPermission, string EditPermission, string Url, bool IsLocked = false);

public interface INotifier
{
    Task NotifyAsync(IEnumerable<Guid> userIds, string title, string? body, RecordInfo? record, bool email = true, CancellationToken ct = default);
}
