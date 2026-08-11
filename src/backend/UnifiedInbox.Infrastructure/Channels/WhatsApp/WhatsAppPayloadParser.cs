using System.Text.Json;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public sealed record WhatsAppInbound(string ExternalMessageId, string CustomerId, string? Text, string? MediaMimeType);
public sealed class WhatsAppPayloadParser
{
    public IReadOnlyList<WhatsAppInbound> Parse(JsonElement payload)
    {
        if (!payload.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return [];
        return messages.EnumerateArray().Select(x => new WhatsAppInbound(x.GetProperty("id").GetString() ?? "", x.GetProperty("from").GetString() ?? "", x.TryGetProperty("text", out var t) ? t.GetProperty("body").GetString() : null, x.TryGetProperty("image", out var i) ? i.GetProperty("mime_type").GetString() : null)).ToList();
    }
}
