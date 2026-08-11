using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/notifications")]
public sealed class NotificationsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet] public IActionResult List() { var tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; return tenant == default ? Unauthorized() : Ok(store.Notifications.Where(x => x.TenantId == tenant)); }
}
