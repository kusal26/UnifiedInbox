namespace UnifiedInbox.Domain;

public enum UserRole { Owner, Admin, Agent }
public enum ConversationStatus { Open, Pending, Closed }
public enum MessageDirection { Inbound, Outbound }
public enum MessageStatus { Pending, Sending, Sent, Delivered, Read, Failed, Unknown }
public enum ActivityKind { Message, InternalNote }
public enum AttachmentStatus { Staged, Claimed, Expired, Rejected }
public enum OutboxStatus { Pending, Processing, Processed, DeadLettered }
public enum WebhookStatus { Received, Processing, Processed, Failed, Ignored }
public interface ITenantScoped { Guid TenantId { get; } }

public sealed class Tenant
{
    private Tenant() { }
    public Tenant(Guid id, string slug, string name) { Id = id; Slug = slug; Name = name; }
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public int RetentionDays { get; set; } = 365;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class User : ITenantScoped
{
    private User() { }
    public User(Guid id, Guid tenantId, string email, string displayName, UserRole role) { Id = id; TenantId = tenantId; Email = email; DisplayName = displayName; Role = role; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Channel : ITenantScoped
{
    private Channel() { }
    public Channel(Guid id, Guid tenantId, string platform, string externalAccountId, bool isHealthy = true) { Id = id; TenantId = tenantId; Platform = platform; ExternalAccountId = externalAccountId; IsHealthy = isHealthy; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Platform { get; set; } = "whatsapp";
    public string ExternalAccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsHealthy { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public string Status { get; set; } = "connected";
    public DateTimeOffset? LastWebhookAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
}

public sealed class ChannelCredential : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public string EncryptedAccessToken { get; set; } = "";
    public string EncryptedWebhookSecret { get; set; } = "";
    public int KeyVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

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

public sealed class RefreshToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UserId { get; set; }
    public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; } public Guid? ReplacedById { get; set; }
    /// <summary>Token family for reuse detection. All rotations of one session share a family.</summary>
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum VerificationPurpose { EmailVerification, PasswordReset }

public sealed class VerificationToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UserId { get; set; }
    public string TokenHash { get; set; } = ""; public VerificationPurpose Purpose { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Invitation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Email { get; set; } = "";
    public UserRole Role { get; set; } public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? InvitedById { get; set; }
}

/// <summary>
/// Unscoped routing table. Webhooks resolve tenant/channel from the provider asset id
/// (WhatsApp phone_number_id) BEFORE entering a tenant context. Contains no secrets.
/// </summary>
public sealed class ProviderRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "whatsapp";
    public string ProviderAssetId { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChannelHealth : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid ChannelId { get; set; }
    public bool IsHealthy { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Attachment : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UploaderId { get; set; }
    public Guid? MessageId { get; set; } public string ObjectKey { get; set; } = ""; public string FileName { get; set; } = "";
    public string ContentType { get; set; } = ""; public long Size { get; set; } public AttachmentStatus Status { get; set; } = AttachmentStatus.Staged;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CannedResponseEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Title { get; set; } = "";
    public string Shortcut { get; set; } = ""; public string Content { get; set; } = "";
}

public sealed class NotificationEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Type { get; set; } = "";
    public string Text { get; set; } = ""; public bool IsRead { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEntryEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid? ActorId { get; set; }
    public string Action { get; set; } = ""; public string Resource { get; set; } = ""; public string Metadata { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WebhookReceipt : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid ChannelId { get; set; }
    public string ProviderEventId { get; set; } = ""; public byte[] RawBody { get; set; } = []; public WebhookStatus Status { get; set; } = WebhookStatus.Received;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutboxEvent : ITenantScoped
{
    private OutboxEvent() { }
    public OutboxEvent(Guid id, Guid tenantId, string type, string payload, DateTimeOffset createdAt) { Id = id; TenantId = tenantId; Type = type; Payload = payload; CreatedAt = createdAt; }
    public Guid Id { get; set; } public Guid TenantId { get; set; } public string Type { get; set; } = ""; public string Payload { get; set; } = "";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending; public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; } public string? LastError { get; set; }
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
