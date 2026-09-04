using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public class WhatsAppMessageSender(HttpClient http, IConfiguration configuration, IHostEnvironment environment, IObjectStorage? storage = null)
{
    /// <summary>Legacy text send used for messages that predate durable delivery parts.</summary>
    public virtual async Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token)
    {
        if (TrySendFake(body, out var fake)) return fake;
        return await PostPayloadAsync(db, channel, new WhatsAppTextPayload(contact.Phone, body), token);
    }

    /// <summary>Sends one durable delivery part of an outbound message and returns its provider id.</summary>
    public virtual async Task<string> SendPartAsync(InboxDbContext db, Channel channel, Contact contact, string body, MessageDeliveryPart part, CancellationToken token)
    {
        if (TrySendFake(body, out var fake)) return fake;
        var payload = part.Kind switch
        {
            DeliveryPartKind.Text => (WhatsAppSendPayload)new WhatsAppTextPayload(contact.Phone, body),
            DeliveryPartKind.Template => new WhatsAppTemplatePayload(contact.Phone, part.TemplateName!, part.TemplateLanguage!, part.TemplateComponentsJson),
            DeliveryPartKind.Image or DeliveryPartKind.Video or DeliveryPartKind.Document => await BuildMediaPayloadAsync(db, channel, contact, part, token),
            _ => throw new InvalidOperationException($"Unknown delivery part kind {part.Kind}."),
        };
        return await PostPayloadAsync(db, channel, payload, token);
    }

    private async Task<WhatsAppMediaPayload> BuildMediaPayloadAsync(InboxDbContext db, Channel channel, Contact contact, MessageDeliveryPart part, CancellationToken token)
    {
        if (storage is null) throw new NotSupportedException("Outbound media requires object storage to read the claimed bytes.");
        if (part.AttachmentId is not { } attachmentId) throw new InvalidOperationException("A media delivery part must reference an attachment.");
        // Only bytes already scanned, completed, and claimed onto this message may leave the tenant.
        var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MessageId == part.MessageId, token)
            ?? throw new InboxException("attachment_not_claimed", "The attachment is not claimed to this message.", 409);
        if (attachment.Status != AttachmentStatus.Claimed)
            throw new InboxException("attachment_not_claimed", "Only claimed attachments can be sent.", 409);

        if (part.ProviderMediaId is null)
        {
            var accessToken = await ResolveAccessTokenAsync(db, channel, token);
            await using var content = await storage.OpenReadAsync(attachment.ObjectKey, token);
            var mediaType = attachment.DetectedContentType ?? attachment.ContentType;
            part.ProviderMediaId = await UploadMediaAsync(accessToken, channel.ExternalAccountId, attachment.FileName, mediaType, content, token);
        }
        return new WhatsAppMediaPayload(contact.Phone, part.Kind, part.ProviderMediaId!);
    }

    private bool TrySendFake(string body, out string providerId)
    {
        var fake = configuration.GetValue("WhatsApp:UseFake", environment.IsDevelopment() || environment.IsEnvironment("Test"));
        if (!fake) { providerId = ""; return false; }
        if (body.Contains("[rate-limit]", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Simulated rate limit.", null, HttpStatusCode.TooManyRequests);
        if (body.Contains("[permanent-failure]", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Simulated permanent provider rejection.");
        providerId = $"fake-{Guid.NewGuid():N}";
        return true;
    }

    private async Task<string> PostPayloadAsync(InboxDbContext db, Channel channel, WhatsAppSendPayload payload, CancellationToken token)
    {
        var accessToken = await ResolveAccessTokenAsync(db, channel, token);
        return await PostMessageAsync(accessToken, channel.ExternalAccountId, payload, token);
    }

    private async Task<string> ResolveAccessTokenAsync(InboxDbContext db, Channel channel, CancellationToken token)
    {
        var credential = await db.ChannelCredentials.SingleAsync(x => x.ChannelId == channel.Id, token);
        return CredentialProtector.FromConfiguration(configuration).Unprotect(credential.EncryptedAccessToken);
    }

    private async Task<string> PostMessageAsync(string accessToken, string phoneNumberId, WhatsAppSendPayload payload, CancellationToken token)
    {
        var body = BuildMessageBody(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{Version}/{phoneNumberId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, token);
        await EnsureSendSuccess(response, token);
        using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        return result.RootElement.GetProperty("messages")[0].GetProperty("id").GetString() ?? throw new InvalidOperationException("Provider did not return a message id.");
    }

    private async Task<string> UploadMediaAsync(string accessToken, string phoneNumberId, string fileName, string mediaType, Stream content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{Version}/{phoneNumberId}/media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent(mediaType), "type");
        var file = new StreamContent(content);
        form.Add(file, "file", fileName);
        request.Content = form;
        using var response = await http.SendAsync(request, token);
        await EnsureSendSuccess(response, token);
        using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        return result.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Provider did not return a media id.");
    }

    private static object BuildMessageBody(WhatsAppSendPayload payload)
    {
        var common = new Dictionary<string, object?> { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = payload.To };
        return payload switch
        {
            WhatsAppTextPayload text => With(common, "text", new { body = text.Body }, "type", "text"),
            WhatsAppTemplatePayload template => BuildTemplateMessage(common, template),
            WhatsAppMediaPayload media => With(common, TypeName(media.Kind), new { id = media.MediaId }, "type", TypeName(media.Kind)),
            _ => throw new InvalidOperationException($"Unknown send payload {payload.GetType().Name}."),
        };
    }

    private static object BuildTemplateMessage(Dictionary<string, object?> common, WhatsAppTemplatePayload template)
    {
        var definition = new Dictionary<string, object?>
        {
            ["name"] = template.Name,
            ["language"] = new { code = template.Language },
        };
        if (template.ComponentsJson is { Length: > 0 } raw)
        {
            var components = JsonSerializer.Deserialize<JsonElement>(raw);
            if (components.ValueKind == JsonValueKind.Array && components.GetArrayLength() > 0) definition["components"] = components;
        }
        var body = new Dictionary<string, object?>(common) { ["type"] = "template" };
        body["template"] = definition;
        return body;
    }

    private static object With(Dictionary<string, object?> common, string contentKey, object content, string typeKey, string typeValue)
    {
        var body = new Dictionary<string, object?>(common) { [typeKey] = typeValue };
        body[contentKey] = content;
        return body;
    }

    private static string TypeName(DeliveryPartKind kind) => kind switch
    {
        DeliveryPartKind.Image => "image",
        DeliveryPartKind.Video => "video",
        _ => "document",
    };

    private string Version => configuration["WhatsApp:GraphVersion"] ?? Environment.GetEnvironmentVariable("WHATSAPP_GRAPH_VERSION") ?? "v23.0";

    private static async Task EnsureSendSuccess(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string detail;
        try { detail = await response.Content.ReadAsStringAsync(token); }
        catch { detail = response.StatusCode.ToString(); }
        switch ((int)response.StatusCode)
        {
            case 401 or 403:
                throw new InboxException("channel_authorization_expired", $"WhatsApp rejected the channel credential ({(int)response.StatusCode}).", 502);
            case 429:
                throw new InboxException("provider_rate_limited", "WhatsApp is rate limiting this phone number.", 429);
            case >= 500:
                throw new InboxException("provider_temporarily_unavailable", $"WhatsApp failed transiently ({(int)response.StatusCode}).", 503);
            default:
                throw new InboxException("provider_rejected", $"WhatsApp rejected the message ({(int)response.StatusCode}). {detail}", 422);
        }
    }
}
