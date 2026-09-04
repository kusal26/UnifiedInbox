namespace UnifiedInbox.Domain;

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
    /// <summary>Owning WhatsApp Business Account id, used for webhook (un)subscription.</summary>
    public string? ExternalBusinessId { get; set; }
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

public sealed class ChannelHealth : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid ChannelId { get; set; }
    public bool IsHealthy { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ConnectionAttemptPurpose { Connect, Reauthorize }

/// <summary>
/// Single-use Embedded Signup handshake. Only hashes of the independent <c>state</c> and
/// <c>nonce</c> values are stored; the raw values live ~10 minutes in the browser flow and are
/// bound to the initiating tenant, user, purpose, and (for reauthorization) channel.
/// </summary>
public sealed class ConnectionAttempt : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid InitiatingUserId { get; set; }
    public string StateHash { get; set; } = "";
    public string NonceHash { get; set; } = "";
    public ConnectionAttemptPurpose Purpose { get; set; } = ConnectionAttemptPurpose.Connect;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
