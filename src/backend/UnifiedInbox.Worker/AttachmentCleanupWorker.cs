using UnifiedInbox.Application;

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
                var expired = await attachments.CleanupExpiredAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Attachment cleanup expired {Count} staging records", expired);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Attachment cleanup failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
