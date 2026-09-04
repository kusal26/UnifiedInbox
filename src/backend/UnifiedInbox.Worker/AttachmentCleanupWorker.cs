using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>Periodically expires stale attachment staging records and deletes unclaimed bytes.</summary>
public sealed class AttachmentCleanupWorker(IServiceScopeFactory scopes, ILogger<AttachmentCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public static Task<int> CleanupTenantAsync(IAttachmentService attachments, ITenantExecutionScope tenantScope, Guid tenantId, CancellationToken token) =>
        tenantScope.RunAsync(tenantId, attachments.CleanupExpiredAsync, token);
}
