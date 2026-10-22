using Sequfloh.Application.Records;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Services;

/// <summary>In-app notification shown in the bell list of the person it is for.</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? RecordType { get; set; }
    public Guid? RecordId { get; set; }
    public string? Link { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Writes in-app notifications.</summary>
public sealed class Notifier(AppDbContext db) : INotifier
{
    public async Task NotifyAsync(IEnumerable<Guid> userIds, string title, string? body, RecordInfo? record, bool email = true, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in userIds.Distinct())
            db.Notifications.Add(new Notification { UserId = id, CreatedAt = now, Title = title, Body = body, RecordType = record?.Type, RecordId = record?.Id, Link = record?.Url });
        await db.SaveChangesAsync(ct);
    }
}
