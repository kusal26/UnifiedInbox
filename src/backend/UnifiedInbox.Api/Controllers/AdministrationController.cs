using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController]
public sealed class AdministrationController(IAdministrationService administration) : ControllerBase
{
    [Authorize(Policy = "Admin"), HttpGet("api/v1/users")] public async Task<IActionResult> Users(CancellationToken token) => Ok(await administration.UsersAsync(token));
    [Authorize(Policy = "Admin"), HttpGet("api/v1/channels")] public async Task<IActionResult> Channels(CancellationToken token) => Ok(await administration.ChannelsAsync(token));
    [HttpGet("api/v1/canned-responses")] public async Task<IActionResult> Canned([FromQuery] string? q, CancellationToken token) => Ok(await administration.CannedResponsesAsync(q, token));
    [Authorize(Policy = "Admin"), HttpPost("api/v1/canned-responses")] public async Task<IActionResult> AddCanned(CannedRequest request, CancellationToken token) => Ok(await administration.AddCannedResponseAsync(request.Title, request.Shortcut, request.Content, token));
    [HttpGet("api/v1/notifications")] public async Task<IActionResult> Notifications(CancellationToken token) => Ok(await administration.NotificationsAsync(token));
    [Authorize(Policy = "Owner"), HttpGet("api/v1/audit-logs")] public async Task<IActionResult> Audit([FromQuery] string? q, CancellationToken token) => Ok(await administration.AuditAsync(q, token));
    [HttpGet("api/v1/workspace")] public async Task<IActionResult> Workspace(CancellationToken token) => await administration.WorkspaceAsync(token) is { } item ? Ok(item) : NotFound();
    [Authorize(Policy = "Admin"), HttpPut("api/v1/workspace")] public async Task<IActionResult> Workspace(WorkspaceRequest request, CancellationToken token) => await administration.UpdateWorkspaceAsync(request.Name, request.RetentionDays, token) is { } item ? Ok(item) : NotFound();
}
public sealed record CannedRequest(string Title, string Shortcut, string Content); public sealed record WorkspaceRequest(string Name, int RetentionDays);
