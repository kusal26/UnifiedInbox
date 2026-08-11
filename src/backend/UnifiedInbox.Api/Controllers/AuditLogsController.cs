using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/audit-logs")]
public sealed class AuditLogsController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet] public IActionResult List() { var tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; var user = HttpContext.Items["userId"] is Guid u ? u : default; if (tenant == default || user == default) return Unauthorized(); try { store.EnsureAdmin(tenant, user); return Ok(store.AuditEntries.Where(x => x.TenantId == tenant)); } catch (UnauthorizedAccessException) { return Forbid(); } }
}
