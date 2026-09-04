using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>
/// Periodically expires stale attachment staging records and deletes unclaimed bytes.
/// Tenants are enumerated from the unscoped <see cref="Tenant"/> table and each tenant is
/// drained inside its own execution scope in bounded batches so one tenant cannot starve
/// the worker.
/// </summary>
public sealed class AttachmentCleanupWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(configuration.GetValue("Workers:AttachmentCleanup:InitialDelayMs", 60_000)), stoppingToken);
        var interval = TimeSpan.FromMilliseconds(configuration.GetValue("Workers:AttachmentCleanup:IntervalMs", 300_000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var attachments = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
                var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
                var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantExecutionScope>();
                foreach (var tenantId in await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(stoppingToken))
                {
                    var expired = await CleanupTenantAsync(attachments, tenantScope, tenantId, stoppingToken);
                    if (expired > 0) logger.LogInformation("Attachment cleanup expired {Count} staging records for tenant {TenantId}", expired, tenantId);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Attachment cleanup failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>Drains one tenant inside its execution scope in bounded passes.</summary>
    public static async Task<int> CleanupTenantAsync(IAttachmentService attachments, ITenantExecutionScope tenantScope, Guid tenantId, CancellationToken token) =>
        await tenantScope.RunAsync(tenantId, async scopedToken =>
        {
            var total = 0;
            int batch;
            do
            {
                batch = await attachments.CleanupExpiredAsync(scopedToken);
                total += batch;
            }
            while (batch > 0);
            return total;
        }, token);
}
