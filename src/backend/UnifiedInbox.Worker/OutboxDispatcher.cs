using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.Worker;

public sealed class OutboxDispatcher(InMemoryInboxStore store, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var item in store.Outbox) logger.LogDebug("Dispatching outbox event {EventType} {EventId}", item.Type, item.Id);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
