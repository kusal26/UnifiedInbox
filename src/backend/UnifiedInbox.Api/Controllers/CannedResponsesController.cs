using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/canned-responses")]
public sealed class CannedResponsesController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet] public IActionResult List([FromQuery] string? q = null) { if (!Context(out var tenant, out _)) return Unauthorized(); return Ok(store.CannedResponses.Where(x => x.TenantId == tenant && (q is null || $"{x.Title} {x.Shortcut} {x.Content}".Contains(q, StringComparison.OrdinalIgnoreCase)))); }
    [HttpPost] public IActionResult Add(CannedRequest request) { if (!Context(out var tenant, out var user)) return Unauthorized(); try { return Ok(store.AddCanned(tenant, user, request.Title, request.Shortcut, request.Content)); } catch (UnauthorizedAccessException) { return Forbid(); } }
    private bool Context(out Guid tenant, out Guid user) { tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; user = HttpContext.Items["userId"] is Guid u ? u : default; return tenant != default && user != default; }
}
public sealed record CannedRequest(string Title, string Shortcut, string Content);
