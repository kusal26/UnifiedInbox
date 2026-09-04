using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>
/// Detects stale webhooks: enabled channels that stopped delivering get a health record and
/// an admin notification. Tenants are enumerated from the unscoped <see cref="Tenant"/> table
/// and each tenant is drained inside its own execution scope in bounded batches so one large
/// tenant cannot starve the rest of the fleet.
/// </summary>
public sealed class ChannelHealthMonitor(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ChannelHealthMonitor> logger) : BackgroundService
{
    /// <summary>Upper bound of channels processed in a single pass for one tenant.</summary>
    public const int HealthBatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(configuration.GetValue("Workers:ChannelHealth:InitialDelayMs", 120_000)), stoppingToken);
        var interval = TimeSpan.FromMilliseconds(configuration.GetValue("Workers:ChannelHealth:IntervalMs", 3_600_000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
                var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantExecutionScope>();
                foreach (var tenantId in await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(stoppingToken))
                {
                    await tenantScope.RunAsync(tenantId, async scopedToken =>
                    {
                        int processed;
                        do
                        {
                            processed = await MonitorTenantAsync(db, scopedToken);
                        }
                        while (processed >= HealthBatchSize);
                    }, stoppingToken);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Channel health monitoring failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>Flags one bounded batch of stale channels for the ambient tenant and returns the count flagged.</summary>
    public static async Task<int> MonitorTenantAsync(InboxDbContext db, CancellationToken stoppingToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var candidates = await db.Channels
            .Where(x => x.IsEnabled && x.Status == "connected" && x.LastWebhookAt != null && x.LastWebhookAt < cutoff)
            .Where(x => !db.ChannelHealth.Any(h => h.ChannelId == x.Id && !h.IsHealthy && h.CreatedAt > cutoff && h.Reason == "stale_webhook"))
            .OrderBy(x => x.LastWebhookAt)
            .Take(HealthBatchSize)
            .ToListAsync(stoppingToken);
        foreach (var channel in candidates)
        {
            channel.IsHealthy = false;
            db.ChannelHealth.Add(new ChannelHealth { TenantId = channel.TenantId, ChannelId = channel.Id, IsHealthy = false, Reason = "stale_webhook" });
            db.Notifications.Add(new NotificationEntity { TenantId = channel.TenantId, Type = "channel.unhealthy", Text = $"No webhooks were received for channel {channel.DisplayName} in the last 24 hours." });
            db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "channel.updated", System.Text.Json.JsonSerializer.Serialize(new { id = channel.Id }), DateTimeOffset.UtcNow));
            db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "notification.created", System.Text.Json.JsonSerializer.Serialize(new { id = channel.Id }), DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync(stoppingToken);
        return candidates.Count;
    }
}
