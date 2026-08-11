using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/users")]
public sealed class UsersController(InMemoryInboxStore store) : ControllerBase
{
    [HttpGet] public IActionResult List() => Context(out var tenant, out _) ? Ok(store.Users.Where(x => x.TenantId == tenant)) : Unauthorized();
    [HttpPost] public IActionResult Add(AddUserRequest request) { if (!Context(out var tenant, out var user)) return Unauthorized(); try { store.AddUser(tenant, user, request.Email, request.DisplayName, request.Role); return Ok(); } catch (UnauthorizedAccessException) { return Forbid(); } }
    private bool Context(out Guid tenant, out Guid user) { tenant = HttpContext.Items["tenantId"] is Guid t ? t : default; user = HttpContext.Items["userId"] is Guid u ? u : default; return tenant != default && user != default; }
}
public sealed record AddUserRequest(string Email, string DisplayName, UserRole Role);
