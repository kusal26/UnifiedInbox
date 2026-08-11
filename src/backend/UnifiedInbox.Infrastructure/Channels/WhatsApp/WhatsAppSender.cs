namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public sealed record WhatsAppSendResult(string ProviderRequestId, bool Accepted);
public sealed class WhatsAppSender
{
    public Task<WhatsAppSendResult> SendAsync(string recipient, string body, CancellationToken cancellationToken = default) => Task.FromResult(new WhatsAppSendResult($"local-{Guid.NewGuid():N}", true));
}
