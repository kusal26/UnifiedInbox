using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/invitations")]
public sealed class InvitationsController(IInvitationService invitations) : ControllerBase
{
    [Authorize(Policy = "Admin"), HttpGet] public async Task<IActionResult> List(CancellationToken token) => Ok(await invitations.ListAsync(token));
    [Authorize(Policy = "Admin"), HttpPost] public async Task<IActionResult> Invite(InviteRequest request, CancellationToken token) => Ok(await invitations.InviteAsync(request.Email, request.Role, token));
    [AllowAnonymous, HttpPost("accept")] public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken token) => await invitations.AcceptAsync(request.Token, request.DisplayName, request.Password, token) ? Ok(new { accepted = true }) : BadRequest(Problem(title: "Invalid or expired invitation token", statusCode: 400));
    [Authorize(Policy = "Admin"), HttpDelete("{id:guid}")] public async Task<IActionResult> Revoke(Guid id, CancellationToken token) => await invitations.RevokeAsync(id, token) ? NoContent() : NotFound();
}

public sealed record InviteRequest(string Email, UserRole Role);
public sealed record AcceptInvitationRequest(string Token, string DisplayName, string Password);
