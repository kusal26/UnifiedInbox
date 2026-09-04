using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public class WhatsAppMessageSender(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
{
    /// <summary>Legacy text send used for messages that predate durable delivery parts.</summary>
    public virtual async Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token)
    {
        if (TrySendFake(body, out var fake)) return fake;
        return await SendProviderAsync(db, channel, new { messaging_product = "whatsapp", recipient_type = "individual", to = contact.Phone, type = "text", text = new { body } }, token);
    }

    /// <summary>Sends one durable delivery part of an outbound message and returns its provider id.</summary>
    public virtual async Task<string> SendPartAsync(InboxDbContext db, Channel channel, Contact contact, string body, MessageDeliveryPart part, CancellationToken token)
    {
        if (TrySendFake(body, out var fake)) return fake;
        var payload = part.Kind switch
        {
            DeliveryPartKind.Text => (object)new { messaging_product = "whatsapp", recipient_type = "individual", to = contact.Phone, type = "text", text = new { body } },
            DeliveryPartKind.Template => BuildTemplatePayload(contact, part),
            DeliveryPartKind.Image or DeliveryPartKind.Video or DeliveryPartKind.Document =>
                throw new NotSupportedException("Outbound media delivery parts require the Graph media upload flow."),
            _ => throw new InvalidOperationException($"Unknown delivery part kind {part.Kind}."),
        };
        return await SendProviderAsync(db, channel, payload, token);
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

    private static object BuildTemplatePayload(Contact contact, MessageDeliveryPart part)
    {
        var template = new Dictionary<string, object?>
        {
            ["name"] = part.TemplateName!,
            ["language"] = new { code = part.TemplateLanguage },
        };
        if (part.TemplateComponentsJson is { Length: > 0 } raw)
        {
            var components = JsonSerializer.Deserialize<JsonElement>(raw);
            if (components.ValueKind == JsonValueKind.Array && components.GetArrayLength() > 0) template["components"] = components;
        }
        return new { messaging_product = "whatsapp", recipient_type = "individual", to = contact.Phone, type = "template", template };
    }

    private async Task<string> SendProviderAsync(InboxDbContext db, Channel channel, object payload, CancellationToken token)
    {
        var credential = await db.ChannelCredentials.SingleAsync(x => x.ChannelId == channel.Id, token);
        var key = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? throw new InvalidOperationException("Credentials:MasterKey is required.")); var accessToken = new CredentialProtector(key).Unprotect(credential.EncryptedAccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{configuration["WhatsApp:GraphVersion"] ?? "v23.0"}/{channel.ExternalAccountId}/messages"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode(); using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token)); return result.RootElement.GetProperty("messages")[0].GetProperty("id").GetString() ?? throw new InvalidOperationException("Provider did not return a message id.");
    }
}
