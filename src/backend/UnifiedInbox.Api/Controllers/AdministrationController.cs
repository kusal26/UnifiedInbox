using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController]
public sealed class AdministrationController(IAdministrationService administration) : ControllerBase
{
    [Authorize(Policy = "Admin"), HttpGet("api/v1/users")] public async Task<IActionResult> Users(CancellationToken token) => Ok(await administration.UsersAsync(token));
    [Authorize(Policy = "Owner"), HttpPut("api/v1/users/{id:guid}/role")] public async Task<IActionResult> SetRole(Guid id, SetRoleRequest request, CancellationToken token) => Ok(await administration.SetUserRoleAsync(id, request.Role, token));
    [Authorize(Policy = "Admin"), HttpPut("api/v1/users/{id:guid}/active")] public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken token) => Ok(await administration.SetUserActiveAsync(id, request.IsActive, token));
    [Authorize(Policy = "Admin"), HttpGet("api/v1/channels")] public async Task<IActionResult> Channels(CancellationToken token) => Ok(await administration.ChannelsAsync(token));
    [HttpGet("api/v1/canned-responses")] public async Task<IActionResult> Canned([FromQuery] string? q, CancellationToken token) => Ok(await administration.CannedResponsesAsync(q, token));
    [Authorize(Policy = "Admin"), HttpPost("api/v1/canned-responses")] public async Task<IActionResult> AddCanned(CannedRequest request, CancellationToken token) => Ok(await administration.AddCannedResponseAsync(request.Title, request.Shortcut, request.Content, token));
    [Authorize(Policy = "Admin"), HttpPut("api/v1/canned-responses/{id:guid}")] public async Task<IActionResult> UpdateCanned(Guid id, CannedRequest request, CancellationToken token) => Ok(await administration.UpdateCannedResponseAsync(id, request.Title, request.Shortcut, request.Content, token));
    [Authorize(Policy = "Admin"), HttpDelete("api/v1/canned-responses/{id:guid}")] public async Task<IActionResult> DeleteCanned(Guid id, CancellationToken token) => await administration.DeleteCannedResponseAsync(id, token) ? NoContent() : NotFound();
    [HttpGet("api/v1/notifications")] public async Task<IActionResult> Notifications([FromQuery] bool unreadOnly, CancellationToken token) => Ok(await administration.NotificationsAsync(unreadOnly, token));
    [HttpPost("api/v1/notifications/{id:guid}/read")] public async Task<IActionResult> MarkNotificationRead(Guid id, CancellationToken token) => await administration.MarkNotificationReadAsync(id, token) ? NoContent() : NotFound();
    [HttpPost("api/v1/notifications/read-all")] public async Task<IActionResult> MarkAllNotificationsRead(CancellationToken token) { await administration.MarkAllNotificationsReadAsync(token); return NoContent(); }
    [HttpGet("api/v1/notification-preferences")] public async Task<IActionResult> Preferences(CancellationToken token) => Ok(await administration.NotificationPreferencesAsync(token));
    [HttpPut("api/v1/notification-preferences")] public async Task<IActionResult> SetPreference(SetPreferenceRequest request, CancellationToken token) => Ok(await administration.SetNotificationPreferenceAsync(request.Kind, request.Enabled, token));
    [Authorize(Policy = "Owner"), HttpGet("api/v1/audit-logs")] public async Task<IActionResult> Audit([FromQuery] string? q, CancellationToken token) => Ok(await administration.AuditAsync(q, token));
    [Authorize(Policy = "Owner"), HttpGet("api/v1/audit-logs/export")] public async Task<IActionResult> AuditExport([FromQuery] string? q, CancellationToken token) => File(Encoding.UTF8.GetBytes(await administration.AuditCsvAsync(q, token)), "text/csv", "audit-logs.csv");
    [Authorize(Policy = "Admin"), HttpGet("api/v1/metrics/overview")] public async Task<IActionResult> Overview([FromQuery] int days = 30, CancellationToken token = default) => Ok(await administration.OverviewMetricsAsync(days, token));
    [HttpGet("api/v1/workspace")] public async Task<IActionResult> Workspace(CancellationToken token) => await administration.WorkspaceAsync(token) is { } item ? Ok(item) : NotFound();
    [Authorize(Policy = "Admin"), HttpPut("api/v1/workspace")] public async Task<IActionResult> Workspace(WorkspaceRequest request, CancellationToken token) => await administration.UpdateWorkspaceAsync(request.Name, request.RetentionDays, token) is { } item ? Ok(item) : NotFound();
}
public sealed record CannedRequest(string Title, string Shortcut, string Content); public sealed record WorkspaceRequest(string Name, int RetentionDays);
public sealed record SetRoleRequest(UserRole Role); public sealed record SetActiveRequest(bool IsActive); public sealed record SetPreferenceRequest(string Kind, bool Enabled);
