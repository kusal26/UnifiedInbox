using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>Thin broker adapter: every delivery is processed idempotently from database state, then acknowledged.</summary>
public sealed class MessagingConsumer(IServiceScopeFactory scopes, ConnectionFactory factory, TenantHeaderSigner signer, ILogger<MessagingConsumer> logger) : BackgroundService
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
                var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantExecutionScope>();
                var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
                await ProcessDeliveryAsync(delivery.BasicProperties.Type, id.Value, delivery.BasicProperties.Headers, signer, db, tenantScope, processor, logger, token);
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

    public static async Task ProcessDeliveryAsync(string? type, Guid id, IDictionary<string, object?>? headers, TenantHeaderSigner signer, InboxDbContext db, ITenantExecutionScope tenantScope, MessageProcessor processor, ILogger logger, CancellationToken token)
    {
        if (!signer.TryRead(headers, out var tenantId)) throw new InvalidOperationException("The tenant message header is missing or invalid.");
        await tenantScope.RunAsync(tenantId, async scopedToken =>
        {
            if (type == "webhook.received")
            {
                var persistedTenant = await db.WebhookReceipts.Where(x => x.Id == id).Select(x => (Guid?)x.TenantId).SingleOrDefaultAsync(scopedToken);
                if (persistedTenant != tenantId) throw new InvalidOperationException("The message tenant does not match the webhook receipt.");
                await processor.NormalizeWebhookAsync(id, scopedToken);
            }
            else if (type == "outbound.message.requested")
            {
                var persistedTenant = await db.Messages.Where(x => x.Id == id).Select(x => (Guid?)x.TenantId).SingleOrDefaultAsync(scopedToken);
                if (persistedTenant != tenantId) throw new InvalidOperationException("The message tenant does not match the outbound message.");
                await processor.SendOutboundAsync(id, scopedToken);
            }
            else logger.LogWarning("Ignoring unknown event type {Type}", type);
        }, token);
    }
}

public sealed class TenantHeaderSigner(string key)
{
    public Dictionary<string, object?> Create(Guid tenantId) => new()
    {
        ["tenant-id"] = tenantId.ToString(),
        ["tenant-signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(tenantId.ToString())))
    };

    public bool TryRead(IDictionary<string, object?>? headers, out Guid tenantId)
    {
        tenantId = default;
        if (headers is null || !headers.TryGetValue("tenant-id", out var rawTenant) || !headers.TryGetValue("tenant-signature", out var rawSignature)) return false;
        var tenantText = HeaderText(rawTenant); var signature = HeaderText(rawSignature);
        if (!Guid.TryParse(tenantText, out tenantId) || tenantId == Guid.Empty) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(tenantId.ToString()));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }

    private static string HeaderText(object? value) => value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), _ => value?.ToString() ?? "" };
}
