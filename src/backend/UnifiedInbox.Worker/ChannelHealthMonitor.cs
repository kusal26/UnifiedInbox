using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>Detects stale webhooks: enabled channels that stopped delivering get a health record and an admin notification.</summary>
public sealed class ChannelHealthMonitor(IServiceScopeFactory scopes, ILogger<ChannelHealthMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
                var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                var candidates = await db.Channels.IgnoreQueryFilters()
                    .Where(x => x.IsEnabled && x.Status == "connected" && x.LastWebhookAt != null && x.LastWebhookAt < cutoff)
                    .ToListAsync(stoppingToken);
                foreach (var channel in candidates)
                {
                    var latest = await db.ChannelHealth.IgnoreQueryFilters()
                        .Where(x => x.ChannelId == channel.Id)
                        .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                    if (latest is not null && !latest.IsHealthy && latest.CreatedAt > cutoff && latest.Reason == "stale_webhook") continue;
                    channel.IsHealthy = false;
                    db.ChannelHealth.Add(new ChannelHealth { TenantId = channel.TenantId, ChannelId = channel.Id, IsHealthy = false, Reason = "stale_webhook" });
                    db.Notifications.Add(new NotificationEntity { TenantId = channel.TenantId, Type = "channel.unhealthy", Text = $"No webhooks were received for channel {channel.DisplayName} in the last 24 hours." });
                    db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "channel.updated", System.Text.Json.JsonSerializer.Serialize(new { id = channel.Id }), DateTimeOffset.UtcNow));
                    db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "notification.created", System.Text.Json.JsonSerializer.Serialize(new { id = channel.Id }), DateTimeOffset.UtcNow));
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Channel health monitoring failed");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
