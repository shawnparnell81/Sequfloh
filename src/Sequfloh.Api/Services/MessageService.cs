using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Common;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Messages;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Infrastructure.Modules.Messages;

public sealed record MessageLinkInput(string? LinkKind, Guid? LinkId);
public sealed record IncomingFile(string FileName, string? ContentType, byte[] Bytes);

/// <summary>
/// One-to-one and named group conversations. Who a person may message follows the same site scope as the rest of the app.
/// A role for the whole of a main site is already treated as every site, so those people can message anyone.
/// </summary>
public sealed class MessageService(AppDbContext db, ModuleSupport m, IAccessService access, ICurrentUser current, IFileStorage storage)
{
    public const int MaxBody = 4000;
    public const int MaxTitle = 120;
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFiles = 5;
    const int MaxPeople = 25;
    public static readonly string[] Emojis = ["\uD83D\uDC4D", "\uD83D\uDC4E", "\uD83D\uDE00", "\uD83D\uDE02", "\uD83C\uDF89", "\u2705", "\uD83D\uDC40", "\uD83D\uDD25", "\uD83D\uDE4F", "\uD83D\uDCA1", "\u2764\uFE0F", "\uD83D\uDC4F", "\uD83D\uDE05", "\uD83E\uDD14", "\uD83D\uDE80"];
    static readonly HashSet<string> FileTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".csv", ".xlsx", ".docx" };

    public async Task<object> UnreadCountAsync(CancellationToken ct)
    {
        await RequireUseAsync(ct);
        var me = m.UserId;
        var count = await db.ConversationMembers.AsNoTracking().CountAsync(member => member.UserId == me && db.Messages.Any(msg =>
            msg.ConversationId == member.ConversationId && msg.SenderId != me && msg.DeletedAt == null &&
            (member.LastReadAt == null || msg.CreatedAt > member.LastReadAt)), ct);
        return new { count };
    }

    public async Task<object> PeopleAsync(string? q, CancellationToken ct)
    {
        var scope = await RequireUseAsync(ct);
        var query = PeopleInScope(scope, activeOnly: true, excludeSelf: true);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim() + "%";
            query = query.Where(u => EF.Functions.ILike(u.DisplayName + " " + u.Email, like));
        }
        var rows = await query.OrderBy(u => u.DisplayName).Take(300)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.LastSeenAt }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return rows.Select(u => new { u.Id, u.DisplayName, u.Email, u.LastSeenAt, Online = Presence.IsOnline(u.LastSeenAt, now) });
    }

    public async Task<object> RecordsAsync(string? kind, string? q, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        kind = (kind ?? "").Trim().ToLowerInvariant();
        q = (q ?? "").Trim();
        if (q.Length < 1 || kind is not ("work-order" or "part" or "drawing")) return Array.Empty<object>();
        var like = "%" + q + "%";
        if (kind == "work-order")
        {
            var orders = await m.TryScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct);
            if (orders is null) return Array.Empty<object>();
            return await orders.Where(x => EF.Functions.ILike(x.Number + " " + (x.WorkOrder ?? "") + " " + (x.PartNumber ?? ""), like))
                .OrderByDescending(x => x.CreatedAt).Take(8)
                .Select(x => new { x.Id, Label = x.WorkOrder == null || x.WorkOrder == "" ? x.Number : x.Number + " (" + x.WorkOrder + ")", Kind = "work-order" }).ToListAsync(ct);
        }
        if (kind == "part")
        {
            var parts = await m.TryScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct);
            if (parts is null) return Array.Empty<object>();
            return await parts.Where(x => EF.Functions.ILike(x.PartNumber + " " + x.Name, like)).OrderBy(x => x.PartNumber).Take(8)
                .Select(x => new { x.Id, Label = x.PartNumber + " " + x.Name, Kind = "part" }).ToListAsync(ct);
        }
        var drawings = await m.TryScopeAsync(db.Drawings.AsNoTracking().Where(x => !x.IsArchived), Permissions.SequflohViewDrawings, ct);
        if (drawings is null) return Array.Empty<object>();
        return await drawings.Where(x => EF.Functions.ILike((x.DrawingNumber ?? "") + " " + x.Title, like)).OrderBy(x => x.DrawingNumber).Take(8)
            .Select(x => new { x.Id, Label = (x.DrawingNumber == null || x.DrawingNumber == "" ? "" : x.DrawingNumber + " ") + x.Title, Kind = "drawing" }).ToListAsync(ct);
    }

    public async Task<object> ListAsync(CancellationToken ct)
    {
        await RequireUseAsync(ct);
        return await ListCoreAsync(await MemberConversationIdsAsync(m.UserId, ct), inbox: true, ct);
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        if (!await IsMemberAsync(id, m.UserId, ct)) throw new NotFoundException("Conversation");
        return await ThreadAsync(id, revealDeleted: false, ct);
    }

    public async Task<object> AdminListAsync(CancellationToken ct)
    {
        var scope = await RequireAdminAsync(ct);
        var ids = scope.AllSites
            ? await db.Conversations.AsNoTracking().OrderByDescending(c => c.LastMessageAt).Select(c => c.Id).Take(200).ToListAsync(ct)
            : await AdminIdsAsync(scope, ct);
        return await ListCoreAsync(ids, inbox: false, ct);
    }

    public async Task<object> AdminGetAsync(Guid id, CancellationToken ct)
    {
        var scope = await RequireAdminAsync(ct);
        if (!await AdminCanSeeAsync(id, scope, ct)) throw new NotFoundException("Conversation");
        return await ThreadAsync(id, revealDeleted: true, ct);
    }

    public async Task<object> StartAsync(IReadOnlyList<Guid>? recipientIds, string? title, string? body, MessageLinkInput link, CancellationToken ct, IReadOnlyList<IncomingFile>? files = null)
    {
        var scope = await RequireUseAsync(ct);
        var prepared = PrepareFiles(files);
        var text = CleanBody(body, prepared.Count > 0);
        var me = m.UserId;
        var recipients = (recipientIds ?? []).Where(id => id != me).Distinct().ToList();
        if (recipients.Count == 0) throw new AppException("Choose at least one person.");
        if (recipients.Count > MaxPeople) throw new AppException($"Choose up to {MaxPeople} people.");
        var visible = await PeopleInScope(scope, activeOnly: true, excludeSelf: true).Where(u => recipients.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        if (visible.Count != recipients.Count) throw new AppException("That person isn't available to message.");
        var names = await NamesAsync(recipients, ct);
        var linked = await ResolveLinkAsync(link, ct);
        var group = recipients.Count >= 2;
        string? groupTitle = null;
        if (group)
        {
            groupTitle = (title ?? "").Trim();
            if (groupTitle.Length == 0) throw new AppException("A group needs a name.");
            if (groupTitle.Length > MaxTitle) throw new AppException($"A group name can be up to {MaxTitle} characters.");
        }

        Conversation conversation;
        if (!group)
        {
            var other = recipients[0];
            var existing = await db.Conversations.FirstOrDefaultAsync(c => !c.IsGroup
                && db.ConversationMembers.Count(mbr => mbr.ConversationId == c.Id) == 2
                && db.ConversationMembers.Any(mbr => mbr.ConversationId == c.Id && mbr.UserId == me)
                && db.ConversationMembers.Any(mbr => mbr.ConversationId == c.Id && mbr.UserId == other), ct);
            conversation = existing ?? NewConversation(false, null, recipients.Append(me));
        }
        else conversation = NewConversation(true, groupTitle, recipients.Append(me));

        var message = AddMessage(conversation, text, linked, PreviewText(text, prepared));
        List<string> stored = [];
        try
        {
            stored = await StoreFilesAsync(message, prepared, ct);
            await TouchMineAsync(conversation.Id, ct);
            var who = string.Join(", ", recipients.Select(id => names.GetValueOrDefault(id) ?? "someone"));
            var place = group ? $"group \"{groupTitle}\"" : "a conversation with " + who;
            var said = text.Length == 0 ? "" : ": " + Quote(text);
            var sentence = group
                ? "Started " + place + " with " + who + (text.Length == 0 ? "" : " and sent: " + Quote(text))
                : "Sent a message in " + place + said;
            Audit(message, "MessageSent", sentence + FileWords(prepared) + LinkedWords(linked), await HomeSiteAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await ForgetFilesAsync(stored);
            throw;
        }
        return new { Id = conversation.Id, MessageId = message.Id, conversation.IsGroup };
    }

    public async Task<object> ReplyAsync(Guid conversationId, string? body, MessageLinkInput link, CancellationToken ct, IReadOnlyList<IncomingFile>? files = null)
    {
        await RequireUseAsync(ct);
        var prepared = PrepareFiles(files);
        var conversation = await OpenMemberAsync(conversationId, ct);
        var text = CleanBody(body, prepared.Count > 0);
        var linked = await ResolveLinkAsync(link, ct);
        var message = AddMessage(conversation, text, linked, PreviewText(text, prepared));
        List<string> stored = [];
        try
        {
            stored = await StoreFilesAsync(message, prepared, ct);
            await TouchMineAsync(conversation.Id, ct);
            var others = await OtherNamesAsync(conversation.Id, ct);
            var place = conversation.IsGroup ? $"group \"{conversation.Title}\"" : "a conversation with " + string.Join(" and ", others);
            var said = text.Length == 0 ? "" : ": " + Quote(text);
            Audit(message, "MessageSent", "Sent a message in " + place + said + FileWords(prepared) + LinkedWords(linked), await HomeSiteAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await ForgetFilesAsync(stored);
            throw;
        }
        return new { Id = message.Id, ConversationId = conversation.Id };
    }

    public async Task ToggleReactionAsync(Guid messageId, string? emoji, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        var message = await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId, ct) ?? throw new NotFoundException("Message");
        if (!await IsMemberAsync(message.ConversationId, m.UserId, ct)) throw new NotFoundException("Message");
        if (message.DeletedAt is not null) throw new AppException("That message was deleted.");
        var face = (emoji ?? "").Trim();
        if (!Emojis.Contains(face)) throw new AppException("Pick an emoji from the list.");
        var mine = m.UserId;
        var existing = await db.MessageReactions.FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == mine && r.Emoji == face, ct);
        if (existing is null) db.MessageReactions.Add(new MessageReaction { MessageId = messageId, UserId = mine, Emoji = face });
        else db.MessageReactions.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Stream Stream, string ContentType, string FileName)> OpenFileAsync(Guid id, CancellationToken ct)
    {
        var file = await db.MessageAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("File");
        var message = await db.Messages.AsNoTracking().FirstAsync(x => x.Id == file.MessageId, ct);
        var member = await IsMemberAsync(message.ConversationId, m.UserId, ct);
        var scope = await access.GetScopeAsync(m.UserId, Permissions.MessagesAdmin, ct);
        var admin = !scope.IsEmpty && await AdminCanSeeAsync(message.ConversationId, scope, ct);
        if (!member && !admin) throw new NotFoundException("File");
        if (message.DeletedAt is not null && !admin) throw new NotFoundException("File");
        var stream = await storage.GetAsync(file.StorageKey, ct) ?? throw new NotFoundException("File");
        return (stream, file.ContentType, file.FileName);
    }

    public async Task MarkReadAsync(Guid conversationId, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        var me = m.UserId;
        var member = await db.ConversationMembers.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == me, ct)
            ?? throw new NotFoundException("Conversation");
        member.LastReadAt = m.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task EditAsync(Guid messageId, string? body, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        var message = await OwnMessageAsync(messageId, ct);
        if (message.DeletedAt is not null) throw new AppException("That message was deleted.");
        var text = CleanBody(body);
        if (text == message.Body) return;
        var before = message.Body;
        message.Body = text;
        message.EditedAt = m.Now;
        var conversation = await db.Conversations.FirstAsync(c => c.Id == message.ConversationId, ct);
        if (conversation.LastMessageId == message.Id) conversation.LastMessagePreview = Preview(text);
        conversation.LastMessageAt = m.Now;
        var place = await PlaceAsync(conversation, ct);
        Audit(message, "MessageEdited", "Edited their message in " + place + ": " + Quote(before) + " → " + Quote(text), await HomeSiteAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken ct)
    {
        await RequireUseAsync(ct);
        var message = await OwnMessageAsync(messageId, ct);
        if (message.DeletedAt is not null) return;
        var before = message.Body;
        message.DeletedAt = m.Now;
        var conversation = await db.Conversations.FirstAsync(c => c.Id == message.ConversationId, ct);
        await RefreshTailAsync(conversation, ct);
        var place = await PlaceAsync(conversation, ct);
        Audit(message, "MessageDeleted", "Deleted their message in " + place + " (was " + Quote(before) + ").", await HomeSiteAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    Conversation NewConversation(bool group, string? title, IEnumerable<Guid> memberIds)
    {
        var now = m.Now;
        var conversation = new Conversation { IsGroup = group, Title = title, LastMessageAt = now };
        db.Conversations.Add(conversation);
        foreach (var id in memberIds.Distinct())
            db.ConversationMembers.Add(new ConversationMember { ConversationId = conversation.Id, UserId = id, LastReadAt = id == m.UserId ? now : null });
        return conversation;
    }

    Message AddMessage(Conversation conversation, string text, ResolvedLink? linked, string preview)
    {
        var message = new Message
        {
            ConversationId = conversation.Id, SenderId = m.UserId, Body = text,
            LinkKind = linked?.Kind, LinkId = linked?.Id, LinkLabel = linked?.Label,
        };
        db.Messages.Add(message);
        conversation.LastMessageAt = m.Now;
        conversation.LastMessageId = message.Id;
        conversation.LastMessagePreview = preview;
        return message;
    }

    async Task TouchMineAsync(Guid conversationId, CancellationToken ct)
    {
        var me = m.UserId;
        var mine = db.ConversationMembers.Local.FirstOrDefault(x => x.ConversationId == conversationId && x.UserId == me)
            ?? await db.ConversationMembers.FirstOrDefaultAsync(x => x.ConversationId == conversationId && x.UserId == me, ct);
        if (mine is not null) mine.LastReadAt = m.Now;
    }

    async Task RefreshTailAsync(Conversation conversation, CancellationToken ct)
    {
        var last = await db.Messages.Where(x => x.ConversationId == conversation.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        conversation.LastMessageAt = m.Now;
        if (last is null) { conversation.LastMessageId = null; conversation.LastMessagePreview = null; return; }
        conversation.LastMessageId = last.Id;
        conversation.LastMessagePreview = last.DeletedAt is null ? Preview(last.Body) : "Message deleted";
    }

    async Task<List<object>> ListCoreAsync(List<Guid> ids, bool inbox, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var me = m.UserId;
        var rows = await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderByDescending(c => c.LastMessageAt)
            .Select(c => new { c.Id, c.Title, c.IsGroup, c.LastMessageAt, c.LastMessagePreview }).ToListAsync(ct);
        var members = await db.ConversationMembers.AsNoTracking().Where(x => ids.Contains(x.ConversationId))
            .Join(db.Users.AsNoTracking(), x => x.UserId, u => u.Id, (x, u) => new { x.ConversationId, x.UserId, x.LastReadAt, u.DisplayName, u.LastSeenAt }).ToListAsync(ct);
        var unread = new HashSet<Guid>();
        if (inbox)
        {
            var hits = await db.Messages.AsNoTracking().Where(msg => ids.Contains(msg.ConversationId) && msg.SenderId != me && msg.DeletedAt == null)
                .Join(db.ConversationMembers.AsNoTracking().Where(x => x.UserId == me), msg => msg.ConversationId, x => x.ConversationId, (msg, x) => new { msg.ConversationId, msg.CreatedAt, x.LastReadAt })
                .Where(x => x.LastReadAt == null || x.CreatedAt > x.LastReadAt).Select(x => x.ConversationId).Distinct().ToListAsync(ct);
            unread = hits.ToHashSet();
        }
        return rows.Select(c =>
        {
            var people = members.Where(x => x.ConversationId == c.Id).OrderBy(x => x.DisplayName).ToList();
            var others = people.Where(x => x.UserId != me).Select(x => x.DisplayName).ToList();
            var heading = c.IsGroup ? (c.Title ?? "Group") : (others.Count == 0 ? "Conversation" : string.Join(", ", others));
            var seen = DateTimeOffset.UtcNow;
            return (object)new
            {
                c.Id, c.IsGroup, Heading = heading, c.LastMessageAt, Preview = c.LastMessagePreview, Unread = unread.Contains(c.Id),
                Participants = people.Select(x => new { Id = x.UserId, x.DisplayName, x.LastSeenAt, Online = Presence.IsOnline(x.LastSeenAt, seen) }).ToList(),
            };
        }).ToList();
    }

    async Task<object> ThreadAsync(Guid id, bool revealDeleted, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == id, ct);
        var members = await db.ConversationMembers.AsNoTracking().Where(x => x.ConversationId == id)
            .Join(db.Users.AsNoTracking(), x => x.UserId, u => u.Id, (x, u) => new { x.UserId, u.DisplayName, u.LastSeenAt }).OrderBy(x => x.DisplayName).ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == id).OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct);
        messages.Reverse();
        var names = members.ToDictionary(x => x.UserId, x => x.DisplayName);
        var me = m.UserId;
        var messageIds = messages.Select(x => x.Id).ToList();
        var attachments = await db.MessageAttachments.AsNoTracking().Where(a => messageIds.Contains(a.MessageId))
            .Select(a => new { a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes }).ToListAsync(ct);
        var reactions = await db.MessageReactions.AsNoTracking().Where(r => messageIds.Contains(r.MessageId))
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id, (r, u) => new { r.MessageId, r.Emoji, r.UserId, u.DisplayName }).ToListAsync(ct);
        var cards = await LinkCardsAsync(messages, ct);
        var others = members.Where(x => x.UserId != me).Select(x => x.DisplayName).ToList();
        var seen = DateTimeOffset.UtcNow;
        return new
        {
            conversation.Id, conversation.IsGroup,
            Heading = conversation.IsGroup ? (conversation.Title ?? "Group") : (others.Count == 0 ? "Conversation" : string.Join(", ", others)),
            Participants = members.Select(x => new { Id = x.UserId, x.DisplayName, x.LastSeenAt, Online = Presence.IsOnline(x.LastSeenAt, seen) }).ToList(),
            CanSend = members.Any(x => x.UserId == me),
            Messages = messages.Select(msg =>
            {
                var deleted = msg.DeletedAt is not null;
                var hide = deleted && !revealDeleted;
                return new
                {
                    msg.Id, msg.SenderId, SenderName = names.GetValueOrDefault(msg.SenderId) ?? "Someone",
                    Body = hide ? null : msg.Body, msg.CreatedAt, msg.EditedAt, Deleted = deleted,
                    LinkKind = hide ? null : msg.LinkKind, LinkId = hide ? (Guid?)null : msg.LinkId, LinkLabel = hide ? null : msg.LinkLabel,
                    LinkPath = hide ? null : PathOf(msg.LinkKind, msg.LinkId),
                    LinkCard = hide || msg.LinkId is not { } lid || !cards.TryGetValue((msg.LinkKind ?? "") + ":" + lid, out var card) ? null : card,
                    Attachments = hide ? [] : attachments.Where(a => a.MessageId == msg.Id).Select(a => new { a.Id, a.FileName, a.ContentType, a.SizeBytes, Image = a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) }).ToList(),
                    Reactions = hide ? [] : reactions.Where(r => r.MessageId == msg.Id).GroupBy(r => r.Emoji).Select(g => new
                    {
                        Emoji = g.Key, Count = g.Count(), Mine = g.Any(x => x.UserId == me), Names = g.Select(x => x.DisplayName).OrderBy(n => n).ToList(),
                    }).OrderBy(x => x.Emoji).ToList(),
                };
            }).ToList(),
        };
    }

    async Task<List<Guid>> MemberConversationIdsAsync(Guid userId, CancellationToken ct) =>
        await db.ConversationMembers.AsNoTracking().Where(x => x.UserId == userId)
            .Join(db.Conversations.AsNoTracking(), x => x.ConversationId, c => c.Id, (x, c) => new { c.Id, c.LastMessageAt })
            .OrderByDescending(x => x.LastMessageAt).Select(x => x.Id).Take(200).ToListAsync(ct);

    async Task<List<Guid>> AdminIdsAsync(AccessScope scope, CancellationToken ct)
    {
        var people = PeopleInScope(scope, activeOnly: false, excludeSelf: false);
        return await db.Conversations.AsNoTracking()
            .Where(c => db.ConversationMembers.Any(member => member.ConversationId == c.Id && people.Any(u => u.Id == member.UserId)))
            .OrderByDescending(c => c.LastMessageAt).Select(c => c.Id).Take(200).ToListAsync(ct);
    }

    async Task<bool> AdminCanSeeAsync(Guid id, AccessScope scope, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, ct)) return false;
        if (scope.AllSites) return true;
        var people = PeopleInScope(scope, activeOnly: false, excludeSelf: false);
        return await db.Conversations.AnyAsync(c => c.Id == id && db.ConversationMembers.Any(member => member.ConversationId == c.Id && people.Any(u => u.Id == member.UserId)), ct);
    }

    async Task<bool> IsMemberAsync(Guid conversationId, Guid userId, CancellationToken ct) =>
        await db.ConversationMembers.AnyAsync(x => x.ConversationId == conversationId && x.UserId == userId, ct);

    async Task<Conversation> OpenMemberAsync(Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(id, m.UserId, ct)) throw new NotFoundException("Conversation");
        return await db.Conversations.FirstAsync(c => c.Id == id, ct);
    }

    async Task<Message> OwnMessageAsync(Guid id, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Message");
        if (!await IsMemberAsync(message.ConversationId, m.UserId, ct)) throw new NotFoundException("Message");
        if (message.SenderId != m.UserId) throw new ForbiddenException("You can only change your own message.");
        return message;
    }

    async Task<string> PlaceAsync(Conversation conversation, CancellationToken ct)
    {
        if (conversation.IsGroup) return $"group \"{conversation.Title}\"";
        var others = await OtherNamesAsync(conversation.Id, ct);
        return others.Count == 0 ? "a conversation" : "a conversation with " + string.Join(" and ", others);
    }

    async Task<List<string>> OtherNamesAsync(Guid conversationId, CancellationToken ct)
    {
        var me = m.UserId;
        return await db.ConversationMembers.AsNoTracking().Where(x => x.ConversationId == conversationId && x.UserId != me)
            .Join(db.Users.AsNoTracking(), x => x.UserId, u => u.Id, (x, u) => u.DisplayName).OrderBy(n => n).ToListAsync(ct);
    }

    async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

    async Task<Guid?> HomeSiteAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == m.UserId).Select(u => u.DefaultSiteId).FirstOrDefaultAsync(ct);

    IQueryable<ApplicationUser> PeopleInScope(AccessScope scope, bool activeOnly, bool excludeSelf)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (activeOnly) query = query.Where(u => u.IsActive);
        if (excludeSelf) { var me = m.UserId; query = query.Where(u => u.Id != me); }
        if (scope.AllSites) return query;
        if (scope.IsEmpty) return query.Where(_ => false);
        var siteWide = scope.SiteIds.ToArray();
        var deptIds = scope.DepartmentPairs.Select(p => p.Dept).ToArray();
        var deptSites = scope.DepartmentPairs.Select(p => p.Site).Distinct().ToArray();
        return query.Where(u =>
            (u.DefaultSiteId != null && siteWide.Contains(u.DefaultSiteId.Value))
            || (u.DefaultSiteId != null && deptSites.Contains(u.DefaultSiteId.Value) && (u.DepartmentId == null || deptIds.Contains(u.DepartmentId.Value)))
            || db.UserRoleAssignments.Any(a => a.UserId == u.Id && (
                a.SiteId == null
                || (a.SiteId != null && siteWide.Contains(a.SiteId.Value))
                || (a.DepartmentId != null && deptIds.Contains(a.DepartmentId.Value))
                || (a.SiteId != null && a.DepartmentId == null && deptSites.Contains(a.SiteId.Value)))));
    }

    async Task<AccessScope> RequireUseAsync(CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(m.UserId, Permissions.MessagesUse, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        return scope;
    }

    async Task<AccessScope> RequireAdminAsync(CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(m.UserId, Permissions.MessagesAdmin, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        return scope;
    }

    sealed record ResolvedLink(string Kind, Guid Id, string Label);

    async Task<ResolvedLink?> ResolveLinkAsync(MessageLinkInput link, CancellationToken ct)
    {
        var kind = (link.LinkKind ?? "").Trim().ToLowerInvariant();
        if (kind.Length == 0 && link.LinkId is null) return null;
        if (kind.Length == 0 || link.LinkId is not { } id) throw new AppException("Choose a work order, part or drawing to link.");
        if (kind == "work-order")
        {
            var orders = await m.TryScopeAsync(db.SequflohTrackingRecords.AsNoTracking(), Permissions.SequflohAccess, ct);
            var order = orders is null ? null : await orders.Where(x => x.Id == id).Select(x => new { x.Number, x.WorkOrder }).FirstOrDefaultAsync(ct);
            if (order is null) throw new AppException("That record can't be linked.");
            var label = string.IsNullOrWhiteSpace(order.WorkOrder) ? order.Number : $"{order.Number} ({order.WorkOrder})";
            return new ResolvedLink(kind, id, label);
        }
        if (kind == "part")
        {
            var parts = await m.TryScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohAccess, ct);
            var part = parts is null ? null : await parts.Where(x => x.Id == id).Select(x => new { x.PartNumber, x.Name }).FirstOrDefaultAsync(ct);
            if (part is null) throw new AppException("That record can't be linked.");
            return new ResolvedLink(kind, id, part.PartNumber + " " + part.Name);
        }
        if (kind == "drawing")
        {
            var drawings = await m.TryScopeAsync(db.Drawings.AsNoTracking().Where(x => !x.IsArchived), Permissions.SequflohViewDrawings, ct);
            var drawing = drawings is null ? null : await drawings.Where(x => x.Id == id).Select(x => new { x.DrawingNumber, x.Title }).FirstOrDefaultAsync(ct);
            if (drawing is null) throw new AppException("That record can't be linked.");
            var label = string.IsNullOrWhiteSpace(drawing.DrawingNumber) ? drawing.Title : drawing.DrawingNumber + " " + drawing.Title;
            return new ResolvedLink(kind, id, label);
        }
        throw new AppException("Choose a work order, part or drawing to link.");
    }

    static string? PathOf(string? kind, Guid? id) => kind switch
    {
        "work-order" when id is { } g => "/sequfloh/record/" + g,
        "part" when id is { } g => "/sequfloh/parts/" + g,
        "drawing" when id is { } g => "/sequfloh/drawings/d/" + g,
        _ => null,
    };

    static string LinkedWords(ResolvedLink? link) => link is null ? "" : link.Kind switch
    {
        "work-order" => " Linked work order " + link.Label + ".",
        "part" => " Linked part " + link.Label + ".",
        "drawing" => " Linked drawing " + link.Label + ".",
        _ => "",
    };

    void Audit(Message message, string action, string description, Guid? siteId) =>
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = current.UserId, UserName = current.UserName, Action = action,
            EntityType = nameof(Message), EntityId = message.Id, Description = AuditText.Fit(description),
            SiteId = siteId, CorrelationId = current.CorrelationId,
            Changes = JsonSerializer.Serialize(new { message.Body, message.DeletedAt, message.EditedAt }),
        });

    static string CleanBody(string? body, bool allowEmpty = false)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0)
        {
            if (allowEmpty) return "";
            throw new AppException("Type a message.");
        }
        if (text.Length > MaxBody) throw new AppException($"A message can be up to {MaxBody} characters.");
        return text;
    }

    sealed record PreparedFile(string Name, string Ext, string ContentType, byte[] Bytes);

    List<PreparedFile> PrepareFiles(IReadOnlyList<IncomingFile>? files)
    {
        var list = (files ?? []).Where(f => f.Bytes is { Length: > 0 }).ToList();
        if (list.Count == 0) return [];
        if (list.Count > MaxFiles) throw new AppException($"A message can include up to {MaxFiles} files.");
        var prepared = new List<PreparedFile>();
        foreach (var file in list)
        {
            if (file.Bytes.LongLength > MaxFileBytes) throw new AppException("Files are limited to 10 MB.");
            var name = Path.GetFileName(file.FileName ?? "").Trim();
            if (name.Length == 0) throw new AppException("Choose a file.");
            if (name.Length > 200) name = name[^200..];
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!FileTypes.Contains(ext)) throw new AppException("This file type is not accepted. Use a picture, PDF, text, CSV, Excel or Word file.");
            prepared.Add(new PreparedFile(name, ext, TypeFor(ext), file.Bytes));
        }
        return prepared;
    }

    async Task<List<string>> StoreFilesAsync(Message message, List<PreparedFile> files, CancellationToken ct)
    {
        var keys = new List<string>();
        try
        {
            foreach (var file in files)
            {
                var row = new MessageAttachment
                {
                    MessageId = message.Id, FileName = file.Name, ContentType = file.ContentType, SizeBytes = file.Bytes.LongLength,
                    StorageKey = $"messages/{message.ConversationId:N}/{Guid.CreateVersion7():N}{file.Ext}",
                };
                db.MessageAttachments.Add(row);
                keys.Add(row.StorageKey);
                await storage.PutAsync(row.StorageKey, new MemoryStream(file.Bytes), file.ContentType, ct);
            }
        }
        catch
        {
            await ForgetFilesAsync(keys);
            throw;
        }
        return keys;
    }

    async Task ForgetFilesAsync(List<string> keys)
    {
        foreach (var key in keys)
            try { await storage.DeleteAsync(key, CancellationToken.None); } catch { /* the send already failed; a leftover file is removed on the next try */ }
    }

    async Task<Dictionary<string, object>> LinkCardsAsync(List<Message> messages, CancellationToken ct)
    {
        var cards = new Dictionary<string, object>();
        var orders = messages.Where(x => x.LinkKind == "work-order" && x.LinkId is not null).Select(x => x.LinkId!.Value).Distinct().ToList();
        var parts = messages.Where(x => x.LinkKind == "part" && x.LinkId is not null).Select(x => x.LinkId!.Value).Distinct().ToList();
        var drawings = messages.Where(x => x.LinkKind == "drawing" && x.LinkId is not null).Select(x => x.LinkId!.Value).Distinct().ToList();
        if (orders.Count > 0)
        {
            var rows = await db.SequflohTrackingRecords.AsNoTracking().Where(x => orders.Contains(x.Id))
                .Select(x => new { x.Id, x.Number, x.WorkOrder, x.PartNumber, x.PartDescription, x.Status, x.Findings }).ToListAsync(ct);
            var suppliers = await db.SequflohChargebackReports.AsNoTracking()
                .Where(c => c.TrackingRecordId != null && orders.Contains(c.TrackingRecordId.Value) && c.Supplier != null && c.Supplier != "")
                .OrderByDescending(c => c.CreatedAt).Select(c => new { Id = c.TrackingRecordId!.Value, c.Supplier }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var supplier = ReadSupplier(row.Findings) ?? suppliers.FirstOrDefault(s => s.Id == row.Id)?.Supplier;
                cards["work-order:" + row.Id] = new
                {
                    Kind = "work-order", row.Id, Number = row.Number, WorkOrder = row.WorkOrder, Part = row.PartNumber, PartName = row.PartDescription,
                    Status = row.Status.ToString(), Supplier = supplier, Path = "/sequfloh/record/" + row.Id,
                };
            }
        }
        if (parts.Count > 0)
        {
            var rows = await db.Parts.AsNoTracking().Where(x => parts.Contains(x.Id)).Select(x => new { x.Id, x.PartNumber, x.Name, x.Status }).ToListAsync(ct);
            foreach (var row in rows)
                cards["part:" + row.Id] = new { Kind = "part", row.Id, Number = row.PartNumber, Part = row.PartNumber, PartName = row.Name, Status = row.Status.ToString(), Supplier = (string?)null, Path = "/sequfloh/parts/" + row.Id };
        }
        if (drawings.Count > 0)
        {
            var rows = await db.Drawings.AsNoTracking().Where(x => drawings.Contains(x.Id))
                .Select(x => new { x.Id, x.DrawingNumber, x.Title, x.CurrentRevision }).ToListAsync(ct);
            var revs = await db.DrawingRevisions.AsNoTracking().Where(r => drawings.Contains(r.DrawingId))
                .Select(r => new { r.DrawingId, r.Revision, r.Status }).ToListAsync(ct);
            foreach (var row in rows)
            {
                var status = revs.FirstOrDefault(r => r.DrawingId == row.Id && r.Revision == row.CurrentRevision)?.Status.ToString() ?? "Current";
                cards["drawing:" + row.Id] = new { Kind = "drawing", row.Id, Number = row.DrawingNumber, Part = (string?)null, PartName = row.Title, Status = status, Supplier = (string?)null, Path = "/sequfloh/drawings/d/" + row.Id };
            }
        }
        return cards;
    }

    static string? ReadSupplier(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("supplier", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    var s = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
        }
        catch { /* findings that are not an object simply have no supplier */ }
        return null;
    }

    static string TypeFor(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    static string PreviewText(string text, List<PreparedFile> files)
    {
        if (text.Length > 0) return Preview(text);
        if (files.Count == 1) return files[0].Name;
        return files.Count + " files";
    }

    static string FileWords(List<PreparedFile> files)
    {
        if (files.Count == 0) return "";
        return " Attached " + string.Join(", ", files.Select(f => f.Name + " (" + SizeWords(f.Bytes.LongLength) + ")")) + ".";
    }

    static string SizeWords(long n) => n < 1024 ? n + " B" : n < 1024 * 1024 ? (n / 1024d).ToString("0.#") + " KB" : (n / 1024d / 1024d).ToString("0.#") + " MB";

    static string Preview(string body)
    {
        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 140 ? text : text[..139] + "…";
    }

    static string Quote(string body)
    {
        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 80) text = text[..79] + "…";
        return "\"" + text + "\"";
    }
}
