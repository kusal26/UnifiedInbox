using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/channels")]
public sealed class ChannelsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet] public IActionResult List() => Context(out var tenant) ? Ok(store.Channels.Where(x => x.TenantId == tenant)) : Unauthorized();
    private bool Context(out Guid tenant) { tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; return tenant != default; }
}
