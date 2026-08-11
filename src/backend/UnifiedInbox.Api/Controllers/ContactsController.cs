using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/contacts")]
public sealed class ContactsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet("{id:guid}/conversations")]
    public IActionResult Conversations(Guid id, [FromQuery] int limit = 20) { var tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; if (tenant == default) return Unauthorized(); if (!store.Contacts.Any(x => x.Id == id && x.TenantId == tenant)) return NotFound(); return Ok(store.Conversations.Where(x => x.TenantId == tenant && x.ContactId == id).Take(Math.Clamp(limit, 1, 100))); }
}
