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
            // Re-read membership on every connect: revoked or deactivated members lose realtime access
            // immediately. SignalR dispatch is not covered by the per-request tenant middleware, so the
            // check explicitly opens its own transaction, sets app.current_tenant (forced RLS governs
            // the read), and queries without EF tenant filters.
            if (await IsActiveMemberAsync(tenant, user))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenant}");
                await base.OnConnectedAsync();
                return;
            }
        }
        Context.Abort();
    }

    private async Task<bool> IsActiveMemberAsync(Guid tenant, Guid user)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"select set_config('app.current_tenant', {tenant.ToString()}, true)");
        var count = await db.Database
            .SqlQuery<int>($"select (count(*))::int as \"Value\" from \"Users\" where \"Id\" = {user} and \"TenantId\" = {tenant} and \"IsActive\" and \"EmailVerifiedAt\" is not null")
            .SingleAsync();
        await transaction.CommitAsync();
        return count == 1;
    }
}
