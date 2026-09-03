using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Hubs;

[Authorize]
public sealed class InboxHub(InboxDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(tenantId, out var tenant) && Guid.TryParse(userId, out var user))
        {
            // Re-read membership on every connect: revoked or deactivated members lose realtime access immediately.
            var member = await db.Users.SingleOrDefaultAsync(x => x.Id == user);
            if (member is not null && member.IsActive && member.EmailVerifiedAt is not null)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenant}");
                await base.OnConnectedAsync();
                return;
            }
        }
        Context.Abort();
    }
}
