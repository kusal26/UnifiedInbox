using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/auth")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [AllowAnonymous, HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken token)
    {
        await auth.RegisterAsync(new(request.WorkspaceName, request.WorkspaceSlug, request.DisplayName, request.Email, request.Password), token);
        return Accepted(new { message = "Workspace created. Check your email to verify your account before logging in." });
    }
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken token) { var result = await auth.LoginAsync(request.TenantSlug, request.Email, request.Password, token); if (result is null) return Unauthorized(Problem(title: "Invalid credentials", statusCode: StatusCodes.Status401Unauthorized)); SetRefreshCookie(result.RefreshToken, DateTimeOffset.UtcNow.AddDays(30)); return Ok(new { result.AccessToken, result.AccessTokenExpiresAt }); }
    [AllowAnonymous, HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken token) => await auth.VerifyEmailAsync(request.Token, token) ? Ok(new { verified = true }) : BadRequest(Problem(title: "Invalid or expired verification token", statusCode: 400));
    [AllowAnonymous, HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(ResendVerificationRequest request, CancellationToken token) { await auth.ResendVerificationAsync(request.Email, token); return Accepted(new { message = "If the account exists, a new verification email was sent." }); }
    [AllowAnonymous, HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken token) { await auth.ForgotPasswordAsync(request.Email, token); return Accepted(new { message = "If the account exists, a password reset email was sent." }); }
    [AllowAnonymous, HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken token) => await auth.ResetPasswordAsync(request.Token, request.NewPassword, token) ? Ok(new { reset = true }) : BadRequest(Problem(title: "Invalid or expired reset token", statusCode: 400));
    [AllowAnonymous, HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken token) { if (!Request.Cookies.TryGetValue("refresh_token", out var value)) return Unauthorized(); var result = await auth.RefreshAsync(value, token); if (result is null) { Response.Cookies.Delete("refresh_token"); return Unauthorized(); } SetRefreshCookie(result.RefreshToken, DateTimeOffset.UtcNow.AddDays(30)); return Ok(new { result.AccessToken, result.AccessTokenExpiresAt }); }
    [AllowAnonymous, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken token) { if (Request.Cookies.TryGetValue("refresh_token", out var value)) await auth.RevokeAsync(value, token); Response.Cookies.Delete("refresh_token"); return NoContent(); }
    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken token) => await auth.MeAsync(token) is { } user ? Ok(user) : NotFound();
    [Authorize, HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken token) => Ok(await auth.SessionsAsync(token));
    [Authorize, HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken token) { await auth.RevokeSessionAsync(id, token); return NoContent(); }
    [Authorize, HttpDelete("sessions")]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken token) { await auth.RevokeAllSessionsAsync(token); return NoContent(); }
    private void SetRefreshCookie(string value, DateTimeOffset expires) => Response.Cookies.Append("refresh_token", value, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Expires = expires, Path = "/api/v1/auth" });
}
public sealed record LoginRequest(string TenantSlug, string Email, string Password);
public sealed record RegisterRequest(string WorkspaceName, string WorkspaceSlug, string DisplayName, string Email, string Password);
public sealed record VerifyEmailRequest(string Token);
public sealed record ResendVerificationRequest(string Email);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string NewPassword);
