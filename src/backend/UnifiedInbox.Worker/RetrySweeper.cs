using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

/// <summary>
/// Re-drives stalled work: receipts and outbound messages whose visibility lease expired
/// (crashed workers, lost deliveries) are republished on the retry schedule. Runs on every
/// worker instance; row-version claims in <see cref="UnifiedInbox.Infrastructure.Messaging.MessageProcessor"/>
/// keep redrives idempotent.
/// </summary>
public sealed class RetrySweeper(IServiceScopeFactory scopes, ConnectionFactory factory, ILogger<RetrySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
        await channel.ExchangeDeclareAsync("unified-inbox.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepBatch(channel, stoppingToken); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "Retry sweep failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task SweepBatch(IChannel channel, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
        var now = DateTimeOffset.UtcNow;
        var receipts = await db.WebhookReceipts.IgnoreQueryFilters()
            .Where(x => (x.Status == WebhookStatus.Received || (x.Status == WebhookStatus.Processing && x.Attempts > 0)) && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt).Take(50).ToListAsync(token);
        foreach (var receipt in receipts)
        {
            receipt.AvailableAt = now.AddMinutes(1); // lease while the redelivery is in flight
            await Publish(channel, "webhook.received", receipt.Id, receipt.TenantId, new { receiptId = receipt.Id }, token);
        }
        var messages = await db.Messages.IgnoreQueryFilters()
            .Where(x => (x.Status == MessageStatus.Pending || x.Status == MessageStatus.Sending) && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.NextAttemptAt).Take(50).ToListAsync(token);
        foreach (var message in messages)
        {
            message.NextAttemptAt = now.AddMinutes(1); // lease while the redelivery is in flight
            await Publish(channel, "outbound.message.requested", message.Id, message.TenantId, new { messageId = message.Id }, token);
        }
        await db.SaveChangesAsync(token);
    }

    private static async Task Publish(IChannel channel, string type, Guid id, Guid tenantId, object payload, CancellationToken token)
    {
        var properties = new BasicProperties { Persistent = true, MessageId = $"{type}:{id}", Type = type, Headers = new Dictionary<string, object?> { ["tenant-id"] = tenantId.ToString() } };
        // Publisher confirms are enabled: this completes only once the broker accepted the redelivery.
        await channel.BasicPublishAsync("unified-inbox.events", type, mandatory: true, properties, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), token);
    }
}
