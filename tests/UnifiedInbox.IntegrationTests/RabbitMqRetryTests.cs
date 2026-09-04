using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shouldly;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Worker;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Broker retry behavior: durable TTL retry buckets dead-letter back to the worker exchange, poison
/// deliveries reach the terminal dead-letter queue, and a retried envelope re-drives a real outbound
/// message through the live worker consumer (publisher confirms precede the database ack).
/// </summary>
public sealed class RabbitMqRetryTests : IAsyncLifetime
{
    private const string SigningKey = "test-retry-tenant-header-signing-key";

    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private IConnection connection = null!;
    private IChannel channel = null!;

    public async Task InitializeAsync()
    {
        await rabbit.StartAsync();
        var factory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()), AutomaticRecoveryEnabled = true };
        connection = await factory.CreateConnectionAsync();
        channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(channel);
    }

    public async Task DisposeAsync()
    {
        if (channel is not null) await channel.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
        await rabbit.DisposeAsync();
    }

    [DockerFact]
    public async Task Retry_bucket_holds_the_envelope_then_returns_it_to_the_worker_queue()
    {
        var tenantId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var envelope = new RetryEnvelope(tenantId, entityId, "webhook.received", Attempt: 1, NotBefore: DateTimeOffset.UtcNow);
        var properties = new BasicProperties { Persistent = true, Type = "webhook.received", MessageId = $"webhook.received:{entityId}:1" };
        await channel.BasicPublishAsync(RabbitMqTopology.RetryExchange, "retry.5s", mandatory: true, properties, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));

        // The 5s TTL must delay the envelope: nothing may reach the worker queue immediately.
        for (var attempt = 0; attempt < 10; attempt += 1)
        {
            var early = await channel.BasicGetAsync(RabbitMqTopology.WorkerQueue, autoAck: true);
            early.ShouldBeNull();
            await Task.Delay(250);
        }

        BasicGetResult? delivery = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (delivery is null && DateTimeOffset.UtcNow < deadline)
        {
            delivery = await channel.BasicGetAsync(RabbitMqTopology.WorkerQueue, autoAck: true);
            if (delivery is null) await Task.Delay(300);
        }
        delivery.ShouldNotBeNull();
        delivery!.RoutingKey.ShouldBe("retry.5s"); // Rabbit preserves the bucket routing key on dead-letter
        delivery.BasicProperties.Type.ShouldBe("webhook.received");
        Encoding.UTF8.GetString(delivery.Body.Span).ShouldContain(entityId.ToString());
    }

    [DockerFact]
    public async Task Poison_deliveries_are_dead_lettered_to_the_terminal_queue()
    {
        var messageId = "poison." + Guid.NewGuid();
        var properties = new BasicProperties { Persistent = true, Type = "webhook.received", MessageId = messageId };
        await channel.BasicPublishAsync(RabbitMqTopology.EventsExchange, "webhook.received", mandatory: true, properties, Encoding.UTF8.GetBytes("{\"receiptId\":\"00000000-0000-0000-0000-00000000aabb\"}"));

        var first = await channel.BasicGetAsync(RabbitMqTopology.WorkerQueue, autoAck: false);
        first.ShouldNotBeNull();
        // A poison (nacked with requeue=false after a redelivery) is dead-lettered by the worker
        // queue's DLX to the terminal dead-letter queue.
        await channel.BasicNackAsync(first!.DeliveryTag, multiple: false, requeue: false);

        BasicGetResult? poison = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (poison is null && DateTimeOffset.UtcNow < deadline)
        {
            poison = await channel.BasicGetAsync(RabbitMqTopology.DeadLetterQueue, autoAck: true);
            if (poison is null) await Task.Delay(200);
        }
        poison.ShouldNotBeNull();
        poison!.BasicProperties.MessageId.ShouldBe(messageId);
        (await channel.QueueDeclarePassiveAsync(RabbitMqTopology.DeadLetterQueue)).MessageCount.ShouldBe(0u); // durable: message consumed after ack
    }

    [DockerFact]
    public async Task The_complete_topology_is_declared_with_all_retry_buckets_and_the_dead_letter_queue()
    {
        await Should.NotThrowAsync(async () =>
        {
            await channel.ExchangeDeclarePassiveAsync(RabbitMqTopology.EventsExchange);
            await channel.ExchangeDeclarePassiveAsync(RabbitMqTopology.RetryExchange);
            foreach (var (queue, _, _) in RabbitMqTopology.RetryBuckets) await channel.QueueDeclarePassiveAsync(queue);
            await channel.QueueDeclarePassiveAsync(RabbitMqTopology.WorkerQueue);
            await channel.QueueDeclarePassiveAsync(RabbitMqTopology.RealtimeQueue);
            await channel.QueueDeclarePassiveAsync(RabbitMqTopology.DeadLetterQueue);
        });
    }

    [DockerFact]
    public async Task A_retried_envelope_re_drives_a_legacy_outbound_message_through_the_live_consumer()
    {
        // Local Postgres so the live worker consumer runs against real forced RLS.
        var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();
        try
        {
            await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
            await admin.OpenAsync();
            await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
            await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
            await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
            await using (var owner = Context(postgres.GetConnectionString())) await owner.Database.MigrateAsync();
            var runtimeConnection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;

            var tenantId = Guid.NewGuid();
            var channelId = Guid.NewGuid();
            var contactId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            await using (var owner = Context(postgres.GetConnectionString()))
            {
                owner.Tenants.Add(new Tenant(tenantId, "retry-drive", "Retry"));
                owner.Channels.Add(new Channel(channelId, tenantId, "whatsapp", "phone-rd", true) { IsEnabled = true, Status = "connected" });
                owner.Contacts.Add(new Contact(contactId, tenantId, "whatsapp", "phone-rd", "15550099", "Customer", "+15550099"));
                owner.Conversations.Add(new Conversation { Id = conversationId, TenantId = tenantId, ChannelId = channelId, ContactId = contactId, ExternalConversationId = "15550099", LastCustomerMessageAt = DateTimeOffset.UtcNow });
                owner.Messages.Add(new Message { Id = messageId, TenantId = tenantId, ChannelId = channelId, ConversationId = conversationId, Direction = MessageDirection.Outbound, Body = "retried hello", Status = MessageStatus.Pending, Sequence = 1 });
                await owner.SaveChangesAsync();
            }

            var host = WorkerHost.CreateHost([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = runtimeConnection,
                    ["RabbitMq:Connection"] = rabbit.GetConnectionString(),
                    ["Messaging:TenantHeaderSigningKey"] = SigningKey,
                    ["WhatsApp:UseFake"] = "true",
                });
                builder.Environment.EnvironmentName = "Test";
            }, services =>
            {
                // Only the consumer runs, so this redrive can only come from the broker retry envelope.
                services.RemoveAll<IHostedService>();
                services.AddHostedService<MessagingConsumer>();
            });

            try
            {
                await host.StartAsync();
                await Task.Delay(TimeSpan.FromSeconds(2)); // let the consumer declare and start

                var envelope = new RetryEnvelope(tenantId, messageId, "outbound.message.requested", Attempt: 1, NotBefore: DateTimeOffset.UtcNow);
                var properties = new BasicProperties { Persistent = true, Type = "outbound.message.requested", MessageId = $"outbound.message.requested:{messageId}:1", Headers = SignedHeaders(tenantId) };
                await channel.BasicPublishAsync(RabbitMqTopology.RetryExchange, "retry.5s", mandatory: true, properties, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));

                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await using var db = Context(postgres.GetConnectionString());
                    var message = await db.Messages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == messageId);
                    if (message is { Status: MessageStatus.Sent }) return;
                    await Task.Delay(300);
                }
                throw new TimeoutException("The retried envelope did not drive the message to Sent.");
            }
            finally
            {
                await host.StopAsync(TimeSpan.FromSeconds(5));
                host.Dispose();
            }
        }
        finally
        {
            await postgres.DisposeAsync();
        }
    }

    private Dictionary<string, object?> SignedHeaders(Guid tenantId) => new()
    {
        ["tenant-id"] = tenantId.ToString(),
        ["tenant-signature"] = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes(tenantId.ToString())))
    };

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}
