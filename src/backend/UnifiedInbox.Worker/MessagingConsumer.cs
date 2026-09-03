using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>Thin broker adapter: every delivery is processed idempotently from database state, then acknowledged.</summary>
public sealed class MessagingConsumer(IServiceScopeFactory scopes, ConnectionFactory factory, ILogger<MessagingConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await using var connection = await factory.CreateConnectionAsync(token); await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        await channel.ExchangeDeclareAsync("unified-inbox.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: token);
        await channel.QueueDeclareAsync("unified-inbox.worker", durable: true, exclusive: false, autoDelete: false, cancellationToken: token);
        await channel.QueueBindAsync("unified-inbox.worker", "unified-inbox.events", "webhook.received", cancellationToken: token);
        await channel.QueueBindAsync("unified-inbox.worker", "unified-inbox.events", "outbound.message.requested", cancellationToken: token);
        await channel.BasicQosAsync(0, 8, false, token);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<MessageProcessor>();
                var id = MessageEnvelope.ExtractId(delivery.Body.ToArray());
                if (id is null) { await channel.BasicAckAsync(delivery.DeliveryTag, false, token); return; }
                if (delivery.BasicProperties.Type == "webhook.received") await processor.NormalizeWebhookAsync(id.Value, token);
                else if (delivery.BasicProperties.Type == "outbound.message.requested") await processor.SendOutboundAsync(id.Value, token);
                else logger.LogWarning("Ignoring unknown event type {Type}", delivery.BasicProperties.Type);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            }
            catch (Exception exception)
            {
                // Processor persists retry state before throwing, so a single requeue is safe;
                // poison messages are acknowledged after one redelivery and re-driven by the sweeper.
                logger.LogError(exception, "Messaging job {MessageId} failed", delivery.BasicProperties.MessageId);
                await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: delivery.Redelivered is false, token);
            }
        };
        await channel.BasicConsumeAsync("unified-inbox.worker", autoAck: false, consumer, token);
        await Task.Delay(Timeout.Infinite, token);
    }
}
