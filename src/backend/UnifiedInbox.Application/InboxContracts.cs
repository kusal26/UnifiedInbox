using UnifiedInbox.Domain;

namespace UnifiedInbox.Application;

public interface IInboxStore
{
    IReadOnlyCollection<Tenant> Tenants { get; }
    IReadOnlyCollection<User> Users { get; }
    IReadOnlyCollection<Channel> Channels { get; }
    IReadOnlyCollection<Contact> Contacts { get; }
    IReadOnlyCollection<Conversation> Conversations { get; }
    IReadOnlyCollection<Message> Messages { get; }
    IReadOnlyCollection<InternalNote> Notes { get; }
    IReadOnlyCollection<OutboxEvent> Outbox { get; }
    void AddInbound(Guid tenantId, Guid conversationId, string body, string? externalMessageId);
    Message AddOutbound(Guid tenantId, Guid conversationId, Guid senderId, string body, string idempotencyKey);
    InternalNote AddNote(Guid tenantId, Guid conversationId, Guid authorId, string body);
    Conversation SetStatus(Guid tenantId, Guid conversationId, ConversationStatus status);
    Conversation MarkRead(Guid tenantId, Guid conversationId, long throughSequence);
}

public sealed record ActivityResponse(IReadOnlyList<ActivityItem> Items, string? NextCursor);
public sealed record ConversationSummary(Guid Id, string ContactName, string Platform, string Preview, ConversationStatus Status, bool Unread, DateTimeOffset UpdatedAt);
