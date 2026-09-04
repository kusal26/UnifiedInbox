namespace UnifiedInbox.Domain;

public enum DeliveryPartKind { Text, Template, Image, Video, Document }

/// <summary>
/// One provider message produced by a single outbound timeline item. Text, an approved template,
/// and each attachment are distinct parts with their own provider id, retry state, and delivery
/// status, so a body-plus-media send is reconciled per part while the parent
/// <see cref="Message"/> stays one inbox item.
/// </summary>
public sealed class MessageDeliveryPart : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid MessageId { get; set; }
    public int Position { get; set; }
    public DeliveryPartKind Kind { get; set; }
    public Guid? AttachmentId { get; set; }
    public string? TemplateName { get; set; }
    public string? TemplateLanguage { get; set; }
    /// <summary>The exact template parameters (components) serialized as JSON and persisted with the
    /// requested send shape so a retry or restart resends the approved snapshot unchanged.</summary>
    public string? TemplateComponentsJson { get; set; }
    /// <summary>Provider message id (for WhatsApp a wamid) returned when this part was accepted.</summary>
    public string? ExternalMessageId { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>Idempotency marker persisted before the provider HTTP call so an interrupted send is
    /// reconciled instead of blindly resent.</summary>
    public string? ProviderRequestId { get; set; }
    /// <summary>Optimistic claim token so concurrent workers never send the same part twice.</summary>
    public uint Version { get; set; }
}
