namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public sealed class WhatsAppAdapter
{
    public IReadOnlySet<string> SupportedMediaTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "video/mp4", "application/pdf" };
    public bool SupportsProviderIdempotency => true;
}
