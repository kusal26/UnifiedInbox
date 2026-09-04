namespace UnifiedInbox.Domain;

public enum ConversationStatus { Open, Pending, Closed }
public enum MessageDirection { Inbound, Outbound }
public enum MessageStatus { Pending, Sending, Sent, Delivered, Read, Failed, Unknown }
public enum ActivityKind { Message, InternalNote }

public sealed class Contact : ITenantScoped
{
    private Contact() { }
    public Contact(Guid id, Guid tenantId, string platform, string externalAccountId, string externalCustomerId, string displayName, string phone) { Id = id; TenantId = tenantId; Platform = platform; ExternalAccountId = externalAccountId; ExternalCustomerId = externalCustomerId; DisplayName = displayName; Phone = phone; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Platform { get; set; } = "";
    public string ExternalAccountId { get; set; } = "";
    public string ExternalCustomerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string? Notes { get; set; }
}

public sealed class Conversation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid TenantId { get; set; }
    public required Guid ChannelId { get; set; }
    public required Guid ContactId { get; set; }
    public string ExternalConversationId { get; set; } = "";
    public ConversationStatus Status { get; set; } = ConversationStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCustomerMessageAt { get; set; }
    public long LastReadSequence { get; set; }
    public uint Version { get; set; }
    public void RecordInboundActivity(DateTimeOffset occurredAt) { Status = ConversationStatus.Open; LastCustomerMessageAt = occurredAt; if (occurredAt > UpdatedAt) UpdatedAt = occurredAt; }
}

public sealed class Message : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public required Guid ConversationId { get; set; }
    public required MessageDirection Direction { get; set; }
    public required string Body { get; set; }
    public string? ExternalMessageId { get; set; }
    public string? IdempotencyKey { get; set; }
    public Guid? SenderUserId { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Sent;
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProviderTimestamp { get; set; }
    public long Sequence { get; set; }
    /// <summary>The durable provider-send units for this timeline item; empty for legacy single-send rows.</summary>
    public List<MessageDeliveryPart> DeliveryParts { get; set; } = [];
    /// <summary>Provider-send attempts. Retry timing lives in <see cref="NextAttemptAt"/>; ambiguous outcomes stop retrying.</summary>
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>Optimistic claim token so concurrent workers never send twice.</summary>
    public uint Version { get; set; }
}

public sealed class InternalNote : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid TenantId { get; set; }
    public required Guid ConversationId { get; set; }
    public required Guid AuthorId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Sequence { get; set; }
}

public sealed record ActivityItem(ActivityKind Kind, Guid Id, Guid ConversationId, string Body, DateTimeOffset CreatedAt, long Sequence, Guid? SenderUserId, MessageStatus? Status);

public static class ConversationStatusLabels
{
    public static ConversationStatus Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "action needed" or "open" => ConversationStatus.Open,
        "waiting" or "pending" => ConversationStatus.Pending,
        "done" or "closed" => ConversationStatus.Closed,
        _ => throw new ArgumentException("Unknown conversation status.", nameof(value))
    };
}
