using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace UnifiedInbox.Api.Hubs;

[Authorize]
public sealed class InboxHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (Guid.TryParse(tenantId, out var parsed)) await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{parsed}");
        else Context.Abort();
        await base.OnConnectedAsync();
    }
}
