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
    public virtual async Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token)
    {
        var fake = configuration.GetValue("WhatsApp:UseFake", environment.IsDevelopment() || environment.IsEnvironment("Test"));
        if (fake)
        {
            if (body.Contains("[rate-limit]", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Simulated rate limit.", null, System.Net.HttpStatusCode.TooManyRequests);
            if (body.Contains("[permanent-failure]", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Simulated permanent provider rejection.");
            return $"fake-{Guid.NewGuid():N}";
        }
        var credential = await db.ChannelCredentials.SingleAsync(x => x.ChannelId == channel.Id, token);
        var key = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? throw new InvalidOperationException("Credentials:MasterKey is required.")); var accessToken = new CredentialProtector(key).Unprotect(credential.EncryptedAccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{configuration["WhatsApp:GraphVersion"] ?? "v23.0"}/{channel.ExternalAccountId}/messages"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); request.Content = JsonContent.Create(new { messaging_product = "whatsapp", to = contact.Phone, type = "text", text = new { body } });
        using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode(); using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token)); return result.RootElement.GetProperty("messages")[0].GetProperty("id").GetString() ?? throw new InvalidOperationException("Provider did not return a message id.");
    }
}
