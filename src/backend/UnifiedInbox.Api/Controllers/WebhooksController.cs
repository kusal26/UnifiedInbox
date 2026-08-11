using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/webhooks")]
public sealed class WebhooksController(InMemoryInboxStore store) : ControllerBase
{
    [HttpPost("whatsapp/{channelId:guid}")]
    public IActionResult Receive(Guid channelId, JsonElement payload) { var channel = store.Channels.FirstOrDefault(x => x.Id == channelId); if (channel is null) return NotFound(); var raw = JsonSerializer.SerializeToUtf8Bytes(payload); var externalId = payload.TryGetProperty("externalMessageId", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"); if (!store.PersistWebhook(channelId, externalId, raw)) return Accepted(new { received = true, duplicate = true }); var c = store.EnsureConversation(channel.TenantId); var body = payload.TryGetProperty("body", out var text) ? text.GetString() ?? "" : ""; store.AddInbound(channel.TenantId, c.Id, body, externalId); return Accepted(new { received = true }); }
}
