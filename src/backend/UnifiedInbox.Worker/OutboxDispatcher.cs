using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, ConnectionFactory factory, TenantHeaderSigner signer, IConfiguration configuration, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private TimeSpan PollInterval => TimeSpan.FromMilliseconds(configuration.GetValue("Workers:OutboxDispatch:IntervalMs", 2000));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchBatch(channel, stoppingToken); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(exception, "Outbox dispatch batch failed"); }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchBatch(IChannel channel, CancellationToken token)
    {
        await using var serviceScope = scopes.CreateAsyncScope(); var db = serviceScope.ServiceProvider.GetRequiredService<InboxDbContext>();
        var tenantScope = serviceScope.ServiceProvider.GetRequiredService<ITenantExecutionScope>();
        foreach (var tenantId in await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(token))
            await tenantScope.RunAsync(tenantId, scopedToken => DispatchTenantBatchAsync(channel, db, tenantId, signer, logger, scopedToken), token);
    }

    public static async Task DispatchTenantBatchAsync(IChannel channel, InboxDbContext db, Guid tenantId, TenantHeaderSigner signer, ILogger logger, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = await db.Outbox.Where(x => x.Status == OutboxStatus.Pending && x.AvailableAt <= now).OrderBy(x => x.CreatedAt).Take(50).ToListAsync(token);
        foreach (var job in jobs)
        {
            try
            {
                job.Status = OutboxStatus.Processing; job.Attempts++; await db.SaveChangesAsync(token);
                var properties = new BasicProperties { Persistent = true, MessageId = job.Id.ToString(), Type = job.Type, Headers = signer.Create(tenantId) };
                await channel.BasicPublishAsync("unified-inbox.events", job.Type, mandatory: true, properties, Encoding.UTF8.GetBytes(job.Payload), token);
                // Publisher confirms are enabled on this channel: the publish above only
                // completes once the broker accepted the message.
                job.Status = OutboxStatus.Processed; job.ProcessedAt = DateTimeOffset.UtcNow; job.LastError = null;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox dispatch {OutboxId} failed on attempt {Attempt}", job.Id, job.Attempts);
                job.LastError = exception.GetType().Name;
                if (job.Attempts >= OutboxRetryPolicy.MaxAttempts || !OutboxRetryPolicy.IsTransient(exception))
                {
                    job.Status = OutboxStatus.DeadLettered;
                    db.Notifications.Add(new NotificationEntity { TenantId = job.TenantId, Type = "sync.failed", Text = $"An update ({job.Type}) could not be delivered after {job.Attempts} attempts." });
                    db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), job.TenantId, "notification.created", JsonSerializer.Serialize(new { id = job.Id }), DateTimeOffset.UtcNow));
                }
                else
                {
                    job.Status = OutboxStatus.Pending;
                    job.AvailableAt = DateTimeOffset.UtcNow.Add(OutboxRetryPolicy.NextDelay(job.Attempts));
                }
            }
            await db.SaveChangesAsync(token);
        }
    }
}
