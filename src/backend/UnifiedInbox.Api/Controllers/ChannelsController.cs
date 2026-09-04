using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api.Controllers;

[Authorize, ApiController, Route("api/v1/channels")]
public sealed class ChannelsController(IChannelService channels, IWhatsAppTemplateService templates) : ControllerBase
{
    [Authorize(Policy = "Admin"), HttpPost("connect/attempt")] public async Task<IActionResult> BeginConnect(BeginConnectRequest request, CancellationToken token) => Ok(await channels.BeginConnectAsync(request.DisplayName, token));
    [Authorize(Policy = "Admin"), HttpPost("connect/complete")] public async Task<IActionResult> CompleteConnect(CompleteConnectRequest request, CancellationToken token) => Ok(await channels.CompleteConnectAsync(request.State, request.Code, request.PhoneNumberId, request.BusinessId, request.DisplayName, token));
    [Authorize(Policy = "Admin"), HttpPost("{id:guid}/reauthorize")] public async Task<IActionResult> BeginReauthorize(Guid id, CancellationToken token) => Ok(await channels.BeginReauthorizeAsync(id, token));
    [Authorize(Policy = "Admin"), HttpPost("{id:guid}/test")] public async Task<IActionResult> Test(Guid id, CancellationToken token) => Ok(await channels.TestChannelAsync(id, token));
    [Authorize(Policy = "Admin"), HttpGet("{id:guid}/health")] public async Task<IActionResult> Health(Guid id, CancellationToken token) => Ok(await channels.HealthHistoryAsync(id, token));
    [Authorize(Policy = "Admin"), HttpPut("{id:guid}/enabled")] public async Task<IActionResult> SetEnabled(Guid id, SetEnabledRequest request, CancellationToken token) => Ok(await channels.SetEnabledAsync(id, request.Enabled, token));
    [Authorize(Policy = "Admin"), HttpPost("{id:guid}/disconnect")] public async Task<IActionResult> Disconnect(Guid id, CancellationToken token) { await channels.DisconnectAsync(id, token); return NoContent(); }
    [Authorize(Policy = "Owner"), HttpPost("credentials/rotate")] public async Task<IActionResult> Rotate(CancellationToken token) => Ok(new { rotated = await channels.RotateCredentialsAsync(token) });
    /// <summary>Approved templates for the TemplatePicker. Agents need these to reply outside the
    /// 24-hour window, so the endpoint is tenant-scoped rather than Admin-only.</summary>
    [HttpGet("{id:guid}/templates")] public async Task<IActionResult> Templates(Guid id, CancellationToken token) => Ok(await templates.ApprovedAsync(id, token));
}

public sealed record BeginConnectRequest(string DisplayName);
public sealed record CompleteConnectRequest(string State, string Code, string PhoneNumberId, string BusinessId, string DisplayName);
public sealed record SetEnabledRequest(bool Enabled);
