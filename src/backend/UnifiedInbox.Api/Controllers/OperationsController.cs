using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/operations")]
public sealed class OperationsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet("health")] public IActionResult Health() => Ok(new { status = "ok", outboxPending = store.Outbox.Count, channels = store.Channels.Count(x => x.IsHealthy) });
    [HttpGet("notifications")] public IActionResult Notifications() { var tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; return tenant == default ? Unauthorized() : Ok(store.Notifications.Where(x => x.TenantId == tenant)); }
    [HttpPost("attachments/cleanup")] public IActionResult Cleanup() { store.CleanupExpiredAttachments(); return Ok(); }
}
