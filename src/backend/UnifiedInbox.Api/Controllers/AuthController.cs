using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/auth")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [AllowAnonymous, HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken token) { var result = await auth.RegisterAsync(new(request.WorkspaceName, request.WorkspaceSlug, request.DisplayName, request.Email, request.Password), token); SetRefreshCookie(result.RefreshToken, result.AccessTokenExpiresAt.AddDays(30)); return Created("/api/v1/auth/me", new { result.AccessToken, result.AccessTokenExpiresAt }); }
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken token) { var result = await auth.LoginAsync(request.TenantSlug, request.Email, request.Password, token); if (result is null) return Unauthorized(Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized)); SetRefreshCookie(result.RefreshToken, DateTimeOffset.UtcNow.AddDays(30)); return Ok(new { result.AccessToken, result.AccessTokenExpiresAt }); }
    [AllowAnonymous, HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken token) { if (!Request.Cookies.TryGetValue("refresh_token", out var value)) return Unauthorized(); var result = await auth.RefreshAsync(value, token); if (result is null) { Response.Cookies.Delete("refresh_token"); return Unauthorized(); } SetRefreshCookie(result.RefreshToken, DateTimeOffset.UtcNow.AddDays(30)); return Ok(new { result.AccessToken, result.AccessTokenExpiresAt }); }
    [AllowAnonymous, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken token) { if (Request.Cookies.TryGetValue("refresh_token", out var value)) await auth.RevokeAsync(value, token); Response.Cookies.Delete("refresh_token"); return NoContent(); }
    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken token) => await auth.MeAsync(token) is { } user ? Ok(user) : NotFound();
    private void SetRefreshCookie(string value, DateTimeOffset expires) => Response.Cookies.Append("refresh_token", value, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Expires = expires, Path = "/api/v1/auth" });
}
public sealed record LoginRequest(string TenantSlug, string Email, string Password);
public sealed record RegisterRequest(string WorkspaceName, string WorkspaceSlug, string DisplayName, string Email, string Password);
