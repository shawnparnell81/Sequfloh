using Sequfloh.Domain.Common;

namespace Sequfloh.Domain.Messages;

/// <summary>A one-to-one thread, or a named group thread. The newest message is kept on the row so the list can sort without reading every message.</summary>
public class Conversation : Entity
{
    public string? Title { get; set; }
    public bool IsGroup { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public Guid? LastMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
}

/// <summary>One person in a conversation, with the moment they last opened it. A thread is unread for them while a later message from someone else is still there.</summary>
public class ConversationMember : Entity
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
}

/// <summary>One message. Editing and deleting keep the row; the audit trail records who changed it and what it said.</summary>
public class Message : Entity
{
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset? EditedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>work-order, part or drawing. Empty when the message does not point at a record.</summary>
    public string? LinkKind { get; set; }
    public Guid? LinkId { get; set; }
    public string? LinkLabel { get; set; }
}
