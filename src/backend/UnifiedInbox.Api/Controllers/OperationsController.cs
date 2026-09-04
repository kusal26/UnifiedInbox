using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Controllers;

[ApiController, Route("api/v1/operations")]
public sealed class OperationsController(InboxDbContext db) : ControllerBase
{
    [AllowAnonymous, HttpGet("health")] public IActionResult Health() => Ok(new { status = "ok" });
    [AllowAnonymous, HttpGet("ready")] public async Task<IActionResult> Ready(CancellationToken token) => await db.Database.CanConnectAsync(token) ? Ok(new { status = "ready" }) : throw new InboxException("database_unavailable", "Database unavailable.", 503);
}
