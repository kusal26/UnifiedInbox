using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/auth")]
public sealed class AuthController(InMemoryInboxStore store) : ControllerBase
{
    [HttpPost("login")]
    public IActionResult Login(LoginRequest request) => store.Login(request.TenantSlug, request.Email, request.Password) is { } token ? Ok(new { accessToken = token }) : Unauthorized();
}
public sealed record LoginRequest(string TenantSlug, string Email, string Password);
