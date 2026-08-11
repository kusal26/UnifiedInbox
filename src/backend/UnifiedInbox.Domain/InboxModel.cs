namespace UnifiedInbox.Domain;

public enum UserRole { Owner, Admin, Agent }
public enum ConversationStatus { Open, Pending, Closed }
public enum MessageDirection { Inbound, Outbound }
public enum MessageStatus { Pending, Sending, Sent, Delivered, Read, Failed, Unknown }
public enum ActivityKind { Message, InternalNote }

public sealed record Tenant(Guid Id, string Slug, string Name);
public sealed record User(Guid Id, Guid TenantId, string Email, string DisplayName, UserRole Role);
public sealed record Channel(Guid Id, Guid TenantId, string Platform, string ExternalAccountId, bool IsHealthy = true);
public sealed record Contact(Guid Id, Guid TenantId, string Platform, string ExternalAccountId, string ExternalCustomerId, string DisplayName, string Phone);

public sealed class Conversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid TenantId { get; init; }
    public required Guid ChannelId { get; init; }
    public required Guid ContactId { get; init; }
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long LastReadSequence { get; set; }
}

public sealed class Message
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid TenantId { get; init; }
    public required Guid ConversationId { get; init; }
    public required MessageDirection Direction { get; init; }
    public required string Body { get; init; }
    public string? ExternalMessageId { get; init; }
    public Guid? SenderUserId { get; init; }
    public MessageStatus Status { get; set; } = MessageStatus.Sent;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; init; }
}

public sealed class InternalNote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid TenantId { get; init; }
    public required Guid ConversationId { get; init; }
    public required Guid AuthorId { get; init; }
    public required string Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long Sequence { get; init; }
}

public sealed record ActivityItem(ActivityKind Kind, Guid Id, Guid ConversationId, string Body, DateTimeOffset CreatedAt, long Sequence, Guid? SenderUserId, MessageStatus? Status);
public sealed record OutboxEvent(Guid Id, Guid TenantId, string Type, string Payload, DateTimeOffset CreatedAt);
