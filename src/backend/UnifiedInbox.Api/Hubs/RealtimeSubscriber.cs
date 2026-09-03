using System.Text;
using Microsoft.AspNetCore.SignalR;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace UnifiedInbox.Api.Hubs;

public sealed class RealtimeSubscriber(ConnectionFactory factory, IHubContext<InboxHub> hub, ILogger<RealtimeSubscriber> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await using var connection = await factory.CreateConnectionAsync(token); await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        await channel.ExchangeDeclareAsync("unified-inbox.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: token);
        await channel.QueueDeclareAsync("unified-inbox.realtime", durable: true, exclusive: false, autoDelete: false, cancellationToken: token);
        foreach (var routingKey in new[] { "conversation.*", "message.*", "note.*", "channel.*", "notification.*" }) await channel.QueueBindAsync("unified-inbox.realtime", "unified-inbox.events", routingKey, cancellationToken: token);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                object? tenantHeader = null; delivery.BasicProperties.Headers?.TryGetValue("tenant-id", out tenantHeader); var tenant = tenantHeader switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string value => value, _ => null };
                if (!Guid.TryParse(tenant, out var tenantId) || string.IsNullOrWhiteSpace(delivery.BasicProperties.Type)) throw new InvalidOperationException("Realtime event is missing routing metadata.");
                var payload = Encoding.UTF8.GetString(delivery.Body.Span); await hub.Clients.Group($"tenant:{tenantId}").SendAsync(delivery.BasicProperties.Type, payload, token); await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            }
            catch (Exception exception) { logger.LogError(exception, "Realtime delivery failed"); await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, token); }
        };
        await channel.BasicConsumeAsync("unified-inbox.realtime", autoAck: false, consumer, token); await Task.Delay(Timeout.Infinite, token);
    }
}
