using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>Describes whether a processed delivery scheduled another attempt.</summary>
public readonly record struct DeliveryResult(bool RetryScheduled, int Attempt, string Operation, Guid EntityId, Guid TenantId)
{
    /// <summary>True when a whole-entity retry was scheduled and a broker envelope must be published.</summary>
    public bool RequiresBrokerRetry => RetryScheduled && Attempt > 0;
}

/// <summary>
/// Broker adapter for durable work. Every delivery is processed idempotently from database state.
/// When the processor schedules a whole-entity retry the consumer first publishes a durable retry
/// envelope to the matching TTL bucket (publisher confirms precede the database acknowledgement),
/// then acknowledges the original. Retried envelopes are only processed once the row's persisted
/// schedule is due; until then they are re-enqueued to their bucket.
/// </summary>
public sealed class MessagingConsumer(IServiceScopeFactory scopes, ConnectionFactory factory, TenantHeaderSigner signer, ILogger<MessagingConsumer> logger) : BackgroundService
{
    private const string WebhookOperation = "webhook.received";
    private const string OutboundOperation = "outbound.message.requested";

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        await using var connection = await factory.CreateConnectionAsync(token);
        // The consumer channel reads; a second channel with publisher confirms schedules retries.
        await using var channel = await connection.CreateChannelAsync(cancellationToken: token);
        await using var publisher = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), token);
        await RabbitMqTopology.DeclareAsync(publisher, token);
        await channel.BasicQosAsync(0, 8, false, token);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<MessageProcessor>();
                var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
                var tenantScope = scope.ServiceProvider.GetRequiredService<ITenantExecutionScope>();

                if (RabbitMqTopology.IsRetryRoutingKey(delivery.RoutingKey))
                {
                    await HandleRetriedDeliveryAsync(channel, publisher, delivery, db, tenantScope, processor, token);
                    return;
                }

                var id = MessageEnvelope.ExtractId(delivery.Body.ToArray());
                if (id is null) { await channel.BasicAckAsync(delivery.DeliveryTag, false, token); return; }
                var result = await ProcessDeliveryAsync(delivery.BasicProperties.Type, id.Value, delivery.BasicProperties.Headers, db, tenantScope, processor, token);
                if (result.RequiresBrokerRetry) await PublishRetryAsync(publisher, result, token);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            }
            catch (Exception exception)
            {
                // A single requeue is safe (the processor persists state before returning). Poison
                // messages are nacked with requeue=false on redelivery and dead-lettered to the
                // terminal dead-letter queue by the worker queue's DLX.
                logger.LogError(exception, "Messaging job {MessageId} failed", delivery.BasicProperties.MessageId);
                await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: delivery.Redelivered is false, token);
            }
        };
        await channel.BasicConsumeAsync(RabbitMqTopology.WorkerQueue, autoAck: false, consumer, token);
        await Task.Delay(Timeout.Infinite, token);
    }

    private async Task HandleRetriedDeliveryAsync(IChannel channel, IChannel publisher, BasicDeliverEventArgs delivery, InboxDbContext db, ITenantExecutionScope tenantScope, MessageProcessor processor, CancellationToken token)
    {
        var envelope = ParseEnvelope(delivery.Body.ToArray());
        if (envelope is null) { await channel.BasicAckAsync(delivery.DeliveryTag, false, token); return; }
        if (!await IsDueAsync(db, tenantScope, envelope, token))
        {
            // The database schedule (with its jittered not-before) is still in the future: hold the
            // envelope in its bucket again instead of processing early.
            await PublishEnvelopeAsync(publisher, envelope, token);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
            return;
        }
        var result = await ProcessDeliveryAsync(envelope.Operation, envelope.EntityId, delivery.BasicProperties.Headers, db, tenantScope, processor, token);
        if (result.RequiresBrokerRetry) await PublishRetryAsync(publisher, result, token);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, token);
    }

    private async Task<DeliveryResult> ProcessDeliveryAsync(string? type, Guid id, IDictionary<string, object?>? headers, InboxDbContext db, ITenantExecutionScope tenantScope, MessageProcessor processor, CancellationToken token)
    {
        if (!signer.TryRead(headers, out var tenantId)) throw new InvalidOperationException("The tenant message header is missing or invalid.");
        var result = new DeliveryResult(false, 0, type ?? "", id, tenantId);
        await tenantScope.RunAsync(tenantId, async scopedToken =>
        {
            if (type == WebhookOperation)
            {
                var persistedTenant = await db.WebhookReceipts.Where(x => x.Id == id).Select(x => (Guid?)x.TenantId).SingleOrDefaultAsync(scopedToken);
                if (persistedTenant != tenantId) throw new InvalidOperationException("The message tenant does not match the webhook receipt.");
                var outcome = await processor.NormalizeWebhookAsync(id, scopedToken);
                if (outcome == WebhookOutcome.RetryScheduled)
                {
                    var attempt = await db.WebhookReceipts.Where(x => x.Id == id).Select(x => x.Attempts).SingleOrDefaultAsync(scopedToken);
                    result = new DeliveryResult(true, attempt, WebhookOperation, id, tenantId);
                }
            }
            else if (type == OutboundOperation)
            {
                var persistedTenant = await db.Messages.Where(x => x.Id == id).Select(x => (Guid?)x.TenantId).SingleOrDefaultAsync(scopedToken);
                if (persistedTenant != tenantId) throw new InvalidOperationException("The message tenant does not match the outbound message.");
                var outcome = await processor.SendOutboundAsync(id, scopedToken);
                if (outcome == OutboundOutcome.RetryScheduled)
                {
                    // Whole-entity (legacy) sends carry the attempt on the message row, so a broker
                    // retry is scheduled. Durable part retries keep per-part schedules driven by the
                    // sweeper; they never bump the message attempt, so no envelope is published.
                    var attempt = await db.Messages.Where(x => x.Id == id).Select(x => x.Attempts).SingleOrDefaultAsync(scopedToken);
                    result = new DeliveryResult(true, attempt, OutboundOperation, id, tenantId);
                }
            }
            else logger.LogWarning("Ignoring unknown event type {Type}", type);
        }, token);
        return result;
    }

    /// <summary>True when the row's persisted schedule has passed (or the row is terminal) so the
    /// retried operation can run now without violating the not-before schedule.</summary>
    public static async Task<bool> IsDueAsync(InboxDbContext db, ITenantExecutionScope tenantScope, RetryEnvelope envelope, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return await tenantScope.RunAsync(envelope.TenantId, async scopedToken =>
        {
            if (envelope.Operation == WebhookOperation)
            {
                var row = await db.WebhookReceipts.Where(x => x.Id == envelope.EntityId).Select(x => new { x.Status, x.AvailableAt }).SingleOrDefaultAsync(scopedToken);
                if (row is null) return true;
                return row.Status is WebhookStatus.Processed or WebhookStatus.Ignored or WebhookStatus.Failed || row.AvailableAt <= now;
            }
            if (envelope.Operation == OutboundOperation)
            {
                var row = await db.Messages.Where(x => x.Id == envelope.EntityId).Select(x => new { x.Status, x.NextAttemptAt }).SingleOrDefaultAsync(scopedToken);
                if (row is null) return true;
                return row.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed || row.NextAttemptAt is null || row.NextAttemptAt <= now;
            }
            return true;
        }, token);
    }

    private async Task PublishRetryAsync(IChannel publisher, DeliveryResult result, CancellationToken token) =>
        await PublishEnvelopeAsync(publisher, new RetryEnvelope(result.TenantId, result.EntityId, result.Operation, result.Attempt, DateTimeOffset.UtcNow), token);

    private async Task PublishEnvelopeAsync(IChannel publisher, RetryEnvelope envelope, CancellationToken token)
    {
        var properties = new BasicProperties
        {
            Persistent = true,
            Type = envelope.Operation,
            MessageId = $"{envelope.Operation}:{envelope.EntityId}:{envelope.Attempt}",
            Headers = signer.Create(envelope.TenantId),
        };
        // Publisher confirms are enabled on this channel: the publish only completes once the broker
        // accepted the envelope. The original delivery is acknowledged only after this returns.
        await publisher.BasicPublishAsync(RabbitMqTopology.RetryExchange, RabbitMqTopology.RetryRoutingKey(envelope.Attempt), mandatory: true, properties, JsonSerializer.SerializeToUtf8Bytes(envelope), token);
    }

    private static RetryEnvelope? ParseEnvelope(byte[] body)
    {
        try { return JsonSerializer.Deserialize<RetryEnvelope>(body); }
        catch (JsonException) { return null; }
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
