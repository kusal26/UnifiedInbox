using System.Text.Json;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public enum WhatsAppInboundKind { None, Image, Video, Audio, Document, Sticker }

public sealed record WhatsAppInbound(
    string ExternalMessageId,
    string CustomerId,
    string? Text,
    string? MediaId,
    string? DeclaredMimeType,
    string? FileName,
    WhatsAppInboundKind Kind);

public sealed record WhatsAppStatusUpdate(string ExternalMessageId, string Status, DateTimeOffset? OccurredAt);
public sealed record WhatsAppParsed(IReadOnlyList<WhatsAppInbound> Messages, IReadOnlyList<WhatsAppStatusUpdate> Statuses);

public sealed class WhatsAppPayloadParser
{
    private static readonly (string Field, WhatsAppInboundKind Kind)[] MediaFields =
    [
        ("image", WhatsAppInboundKind.Image),
        ("video", WhatsAppInboundKind.Video),
        ("audio", WhatsAppInboundKind.Audio),
        ("document", WhatsAppInboundKind.Document),
        ("sticker", WhatsAppInboundKind.Sticker),
    ];

    public IReadOnlyList<WhatsAppInbound> Parse(JsonElement payload) => ParseFull(payload).Messages;

    /// <summary>
    /// Normalizes the Cloud API envelope. Unknown events, status-only callbacks, provider
    /// errors, and malformed entries yield empty results instead of throwing; malformed
    /// sibling messages never drop their well-formed peers.
    /// </summary>
    public WhatsAppParsed ParseFull(JsonElement payload)
    {
        var value = Navigate(payload);
        var messages = new List<WhatsAppInbound>();
        var statuses = new List<WhatsAppStatusUpdate>();
        if (value is null) return new(messages, statuses);
        if (value.Value.TryGetProperty("messages", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                try { messages.Add(ParseMessage(item)); } catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) { /* skip malformed entry */ }
        if (value.Value.TryGetProperty("statuses", out var updates) && updates.ValueKind == JsonValueKind.Array)
            foreach (var item in updates.EnumerateArray())
                try { statuses.Add(ParseStatus(item)); } catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException) { /* skip malformed entry */ }
        return new(messages, statuses);
    }

    private static WhatsAppInbound ParseMessage(JsonElement item)
    {
        var id = item.GetProperty("id").GetString() ?? "";
        var from = item.GetProperty("from").GetString() ?? "";
        string? text = null;
        if (item.TryGetProperty("text", out var textNode) && textNode.TryGetProperty("body", out var body)) text = body.GetString();
        string? mediaId = null;
        string? mime = null;
        string? fileName = null;
        var kind = WhatsAppInboundKind.None;
        foreach (var (field, fieldKind) in MediaFields)
            if (item.TryGetProperty(field, out var node))
            {
                kind = fieldKind;
                if (node.TryGetProperty("id", out var media)) mediaId = media.GetString();
                if (node.TryGetProperty("mime_type", out var declared)) mime = declared.GetString();
                if (node.TryGetProperty("filename", out var name)) fileName = name.GetString();
                if (text is null && node.TryGetProperty("caption", out var caption)) text = caption.GetString();
                break;
            }
        return new(id, from, text, mediaId, mime, fileName, kind);
    }

    private static WhatsAppStatusUpdate ParseStatus(JsonElement item)
    {
        var id = item.GetProperty("id").GetString() ?? "";
        var status = item.GetProperty("status").GetString() ?? "unknown";
        DateTimeOffset? occurredAt = null;
        if (item.TryGetProperty("timestamp", out var stamp) && long.TryParse(stamp.GetString(), out var seconds))
            occurredAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return new(id, status, occurredAt);
    }

    private static JsonElement? Navigate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("messages", out _) || payload.TryGetProperty("statuses", out _)) return payload;
        try { return payload.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value"); }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { return null; }
    }
}
