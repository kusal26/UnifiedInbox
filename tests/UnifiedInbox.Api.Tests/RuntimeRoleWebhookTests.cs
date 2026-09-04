using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Tests;

/// <summary>
/// Posts a real WhatsApp webhook through the API host as <c>app_runtime</c> and proves the
/// receipt + outbox rows are created inside the tenant execution scope resolved from the
/// unscoped provider route (no tenant/channel ids are trusted from the payload).
/// </summary>
[Collection("runtime-role")]
public sealed class RuntimeRoleWebhookTests(RuntimeRoleFixture fixture)
{
    [DockerFact]
    public async Task Provider_route_creates_receipt_and_outbox_under_runtime_scope_over_http()
    {
        var tenantId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var phone = "1555" + Random.Shared.Next(100000, 999999).ToString();
        await using (var owner = fixture.Context(fixture.OwnerConnection))
        {
            owner.Tenants.Add(new Tenant(tenantId, $"webhook-{phone}", "Webhook"));
            owner.Channels.Add(new Channel(channelId, tenantId, "whatsapp", phone, true));
            owner.ProviderRoutes.Add(new ProviderRoute { Provider = "whatsapp", ProviderAssetId = phone, TenantId = tenantId, ChannelId = channelId });
            await owner.SaveChangesAsync();
        }

        var eventId = "wamid." + Guid.NewGuid().ToString("N");
        var body = Payload(phone, eventId);
        var client = fixture.Factory.CreateClient();
        var first = await PostWebhookAsync(client, body);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            (await db.WebhookReceipts.CountAsync()).ShouldBe(0); // no ambient tenant => fail closed
            var scope = new TenantExecutionScope(db);
            await scope.RunAsync(tenantId, async token =>
            {
                (await db.WebhookReceipts.CountAsync(token)).ShouldBe(1);
                (await db.WebhookReceipts.SingleAsync(token)).ProviderEventId.ShouldBe(eventId);
                (await db.Outbox.CountAsync(x => x.Type == "webhook.received", token)).ShouldBe(1);
            }, CancellationToken.None);
            await scope.RunAsync(tenantId, async token =>
            {
                (await db.Channels.SingleAsync(x => x.Id == channelId, token)).LastWebhookAt.ShouldNotBeNull();
            }, CancellationToken.None);
        }

        var duplicate = await PostWebhookAsync(client, body);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            var scope = new TenantExecutionScope(db);
            await scope.RunAsync(tenantId, async token => (await db.WebhookReceipts.CountAsync(token)).ShouldBe(1), CancellationToken.None);
        }
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/whatsapp");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Hub-Signature-256", Signature(body));
        return await client.SendAsync(request);
    }

    private string Signature(byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(fixture.AppSecret), body));

    private static byte[] Payload(string phoneNumberId, string eventId) => Encoding.UTF8.GetBytes(
        $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "waba-1",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "1555000000", "phone_number_id": "{{phoneNumberId}}" },
                "messages": [{ "from": "1555000001", "id": "{{eventId}}", "timestamp": "1767000000", "type": "text", "text": { "body": "hello" } }]
              }
            }]
          }]
        }
        """);
}
