using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;

namespace UnifiedInbox.Api.Controllers;

[AllowAnonymous, ApiController, Route("api/v1/webhooks/whatsapp")]
public sealed class WebhooksController(IWebhookService webhooks, WhatsAppSignatureValidator signatures, IConfiguration configuration) : ControllerBase
{
    [HttpGet] public IActionResult Verify([FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? verifyToken, [FromQuery(Name = "hub.challenge")] string? challenge) => mode == "subscribe" && verifyToken == configuration["WhatsApp:VerifyToken"] ? Content(challenge ?? "") : Forbid();
    [HttpPost("{channelId:guid}")]
    public async Task<IActionResult> Receive(Guid channelId, CancellationToken token) { using var memory = new MemoryStream(); await Request.Body.CopyToAsync(memory, token); var body = memory.ToArray(); var secret = configuration["WhatsApp:AppSecret"] ?? ""; if (string.IsNullOrWhiteSpace(secret) || !signatures.IsValid(body, Request.Headers["X-Hub-Signature-256"], secret)) return Unauthorized(); using var json = JsonDocument.Parse(body); var eventId = FindEventId(json.RootElement) ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)); return await webhooks.PersistAsync(channelId, eventId, body, token) ? Ok(new { received = true }) : NotFound(); }
    private static string? FindEventId(JsonElement root) { if (root.TryGetProperty("externalMessageId", out var id)) return id.GetString(); try { return root.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value").GetProperty("messages")[0].GetProperty("id").GetString(); } catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { return null; } }
}
