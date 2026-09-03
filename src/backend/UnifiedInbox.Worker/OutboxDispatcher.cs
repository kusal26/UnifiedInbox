using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Worker;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, ConnectionFactory factory, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync("unified-inbox.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchBatch(channel, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task DispatchBatch(IChannel channel, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>(); var now = DateTimeOffset.UtcNow;
        var jobs = await db.Outbox.IgnoreQueryFilters().Where(x => x.Status == OutboxStatus.Pending && x.AvailableAt <= now).OrderBy(x => x.CreatedAt).Take(50).ToListAsync(token);
        foreach (var job in jobs)
        {
            try
            {
                job.Status = OutboxStatus.Processing; job.Attempts++; await db.SaveChangesAsync(token);
                var properties = new BasicProperties { Persistent = true, MessageId = job.Id.ToString(), Type = job.Type, Headers = new Dictionary<string, object?> { ["tenant-id"] = job.TenantId.ToString() } };
                await channel.BasicPublishAsync("unified-inbox.events", job.Type, mandatory: true, properties, Encoding.UTF8.GetBytes(job.Payload), token);
                job.Status = OutboxStatus.Processed; job.ProcessedAt = DateTimeOffset.UtcNow; job.LastError = null;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox dispatch {OutboxId} failed on attempt {Attempt}", job.Id, job.Attempts);
                job.LastError = exception.GetType().Name; job.Status = job.Attempts >= 8 ? OutboxStatus.DeadLettered : OutboxStatus.Pending;
                job.AvailableAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, job.Attempts)) + Random.Shared.NextDouble());
            }
            await db.SaveChangesAsync(token);
        }
    }
}
