using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Messaging;

namespace UnifiedInbox.Api.Hubs;

public sealed class RealtimeSubscriber(ConnectionFactory factory, IHubContext<InboxHub> hub, ILogger<RealtimeSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await using var connection = await factory.CreateConnectionAsync(token); await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        await RabbitMqTopology.DeclareAsync(channel, token);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                object? tenantHeader = null; delivery.BasicProperties.Headers?.TryGetValue("tenant-id", out tenantHeader); var tenant = tenantHeader switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string value => value, _ => null };
                var type = delivery.BasicProperties.Type;
                if (!Guid.TryParse(tenant, out var tenantId) || string.IsNullOrWhiteSpace(type)) throw new InvalidOperationException("Realtime event is missing routing metadata.");
                // Structured DTO: clients receive a typed envelope and can apply targeted
                // cache updates instead of invalidating everything. The SignalR method name
                // stays the event type, so existing subscribers keep working.
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(delivery.Body.Span));
                await hub.Clients.Group($"tenant:{tenantId}").SendAsync(type, new RealtimeEvent(type, document.RootElement.Clone(), DateTimeOffset.UtcNow), token);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            }
            catch (Exception exception) { logger.LogError(exception, "Realtime delivery failed"); await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, token); }
        };
        await channel.BasicConsumeAsync("unified-inbox.realtime", autoAck: false, consumer, token); await Task.Delay(Timeout.Infinite, token);
    }
}
