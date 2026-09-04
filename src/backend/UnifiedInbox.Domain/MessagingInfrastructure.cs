namespace UnifiedInbox.Domain;

public enum OutboxStatus { Pending, Processing, Processed, DeadLettered }
public enum WebhookStatus { Received, Processing, Processed, Failed, Ignored }

public sealed class WebhookReceipt : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid ChannelId { get; set; }
    public string ProviderEventId { get; set; } = ""; public byte[] RawBody { get; set; } = []; public WebhookStatus Status { get; set; } = WebhookStatus.Received;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Normalization attempts. <see cref="AvailableAt"/> gates the retry sweeper.</summary>
    public int Attempts { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
    /// <summary>Optimistic claim token so concurrent workers never normalize twice.</summary>
    public uint Version { get; set; }
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
