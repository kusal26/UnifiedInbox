using Microsoft.AspNetCore.SignalR;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Api.Hubs;

public sealed class InboxHub(InMemoryInboxStore store) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var authorization = Context.GetHttpContext()?.Request.Headers.Authorization.ToString();
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true && store.TrySession(authorization[7..], out var tenantId, out _)) await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
        await base.OnConnectedAsync();
    }
}
