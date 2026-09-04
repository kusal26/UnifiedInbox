using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using Shouldly;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Worker;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Boots the real worker host (WorkerHost.CreateHost) so its actual BackgroundServices run:
/// the outbox dispatcher publishes to Rabbit, the real consumer drives the message processor
/// inside a tenant scope, and the attachment-cleanup and channel-health hosted services drain
/// bounded per-tenant batches — all as the <c>app_runtime</c> role against forced RLS.
/// </summary>
public sealed class RuntimeRoleWorkerTests : IAsyncLifetime
{
    private const string SigningKey = "test-worker-tenant-header-signing-key";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private string ownerConnection = "";
    private string runtimeConnection = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using (var owner = Context(postgres.GetConnectionString())) await owner.Database.MigrateAsync();
        ownerConnection = postgres.GetConnectionString();
        runtimeConnection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
    }

    public Task DisposeAsync() => Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask());

    [DockerFact]
    public async Task Worker_host_dispatches_consumes_cleans_and_monitors_in_tenant_scopes()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var channelA = Guid.NewGuid();
        var contactA = Guid.NewGuid();
        var conversationA = Guid.NewGuid();
        var messageA = Guid.NewGuid();
        var outboxA = Guid.NewGuid();
        var staleChannel = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using (var owner = Context(ownerConnection))
        {
            owner.Tenants.AddRange(new Tenant(tenantA, "worker-a", "A"), new Tenant(tenantB, "worker-b", "B"));
            owner.Users.AddRange(
                new User(userA, tenantA, "owner-a@example.com", "Owner A", UserRole.Owner) { NormalizedEmail = "OWNER-A@EXAMPLE.COM", PasswordHash = "unused" },
                new User(userB, tenantB, "owner-b@example.com", "Owner B", UserRole.Owner) { NormalizedEmail = "OWNER-B@EXAMPLE.COM", PasswordHash = "unused" });
            owner.Channels.AddRange(
                new Channel(channelA, tenantA, "whatsapp", "phone-a", true) { IsEnabled = true, Status = "connected", LastWebhookAt = DateTimeOffset.UtcNow },
                new Channel(staleChannel, tenantB, "whatsapp", "phone-b", true) { IsEnabled = true, Status = "connected", LastWebhookAt = DateTimeOffset.UtcNow.AddHours(-48) });
            owner.Contacts.Add(new Contact(contactA, tenantA, "whatsapp", "phone-a", "15550001", "Customer", "+15550001"));
            owner.Conversations.Add(new Conversation { Id = conversationA, TenantId = tenantA, ChannelId = channelA, ContactId = contactA, ExternalConversationId = "15550001", LastCustomerMessageAt = DateTimeOffset.UtcNow });
            owner.Messages.Add(new Message { Id = messageA, TenantId = tenantA, ChannelId = channelA, ConversationId = conversationA, Direction = MessageDirection.Outbound, Body = "hello", Status = MessageStatus.Pending, Sequence = 1 });
            owner.Outbox.Add(new OutboxEvent(outboxA, tenantA, "outbound.message.requested", JsonSerializer.Serialize(new { messageId = messageA }), DateTimeOffset.UtcNow));
            owner.Attachments.AddRange(
                ExpiredAttachment(tenantB, userB, "expired-1.pdf"),
                ExpiredAttachment(tenantB, userB, "expired-2.pdf"));
            await owner.SaveChangesAsync();
        }

        await using var broker = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync();
        await using var setup = await broker.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(setup);

        var host = WorkerHost.CreateHost([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = runtimeConnection,
                ["RabbitMq:Connection"] = rabbit.GetConnectionString(),
                ["Messaging:TenantHeaderSigningKey"] = SigningKey,
                ["WhatsApp:UseFake"] = "true",
                ["Workers:OutboxDispatch:IntervalMs"] = "100",
                ["Workers:RetrySweep:IntervalMs"] = "300",
                ["Workers:ChannelHealth:InitialDelayMs"] = "100",
                ["Workers:ChannelHealth:IntervalMs"] = "300",
                ["Workers:AttachmentCleanup:InitialDelayMs"] = "100",
                ["Workers:AttachmentCleanup:IntervalMs"] = "300",
            });
            builder.Environment.EnvironmentName = "Test";
        });

        try
        {
            await host.StartAsync();

            await WaitUntilAsync(async () =>
            {
                await using var db = Context(ownerConnection);
                var message = await db.Messages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == messageA);
                var job = await db.Outbox.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == outboxA);
                return message is { Status: MessageStatus.Sent } && job is { Status: OutboxStatus.Processed };
            }, TimeSpan.FromSeconds(25));

            await WaitUntilAsync(async () =>
            {
                await using var db = Context(ownerConnection);
                var expired = await db.Attachments.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantB && x.Status == AttachmentStatus.Expired);
                var health = await db.ChannelHealth.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantB && x.Reason == "stale_webhook");
                return expired >= 2 && health >= 1;
            }, TimeSpan.FromSeconds(25));

            await using (var db = Context(ownerConnection))
            {
                var message = await db.Messages.IgnoreQueryFilters().SingleAsync(x => x.Id == messageA);
                message.Status.ShouldBe(MessageStatus.Sent);
                message.ExternalMessageId.ShouldNotBeNullOrWhiteSpace();
                (await db.Outbox.IgnoreQueryFilters().SingleAsync(x => x.Id == outboxA)).Status.ShouldBe(OutboxStatus.Processed);
                (await db.Attachments.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantB && x.Status == AttachmentStatus.Expired)).ShouldBe(2);
                (await db.ChannelHealth.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantB && x.Reason == "stale_webhook")).ShouldBe(1);
                (await db.Notifications.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantB && x.Type == "channel.unhealthy")).ShouldBeTrue();
                (await db.ChannelHealth.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantA)).ShouldBe(0);
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }

    [DockerFact]
    public async Task Consumer_rejects_a_message_whose_header_tenant_mismatches_the_record()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        await using (var owner = Context(ownerConnection))
        {
            owner.Tenants.AddRange(new Tenant(tenantA, "mismatch-a", "A"), new Tenant(tenantB, "mismatch-b", "B"));
            owner.Channels.Add(new Channel(channelId, tenantA, "whatsapp", "phone-mm", true) { IsEnabled = true, Status = "connected" });
            owner.Contacts.Add(new Contact(contactId, tenantA, "whatsapp", "phone-mm", "15550002", "C", "+15550002"));
            owner.Conversations.Add(new Conversation { Id = conversationId, TenantId = tenantA, ChannelId = channelId, ContactId = contactId, ExternalConversationId = "15550002", LastCustomerMessageAt = DateTimeOffset.UtcNow });
            owner.Messages.Add(new Message { Id = messageId, TenantId = tenantA, ChannelId = channelId, ConversationId = conversationId, Direction = MessageDirection.Outbound, Body = "hello", Status = MessageStatus.Pending, Sequence = 1 });
            await owner.SaveChangesAsync();
        }

        await using var broker = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync();
        await using var publish = await broker.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(publish);
        // The header is VALID but routes to tenant B while the message record belongs to tenant A:
        // the consumer must reject the mismatch instead of sending.
        var validForOtherTenant = new Dictionary<string, object?>
        {
            ["tenant-id"] = tenantB.ToString(),
            ["tenant-signature"] = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes(tenantB.ToString())))
        };
        var properties = new BasicProperties { Persistent = true, Type = "outbound.message.requested", Headers = validForOtherTenant };
        await publish.BasicPublishAsync("unified-inbox.events", "outbound.message.requested", mandatory: true, properties, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { messageId })));

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
            // Only the real consumer runs: no dispatcher/sweeper can republish a valid copy.
            services.RemoveAll<IHostedService>();
            services.AddHostedService<MessagingConsumer>();
        });
        try
        {
            await host.StartAsync();
            // The mismatched delivery must be rejected (nacked), never sent: the message stays Pending.
            await Task.Delay(TimeSpan.FromSeconds(4));
            await using var db = Context(ownerConnection);
            var message = await db.Messages.IgnoreQueryFilters().SingleAsync(x => x.Id == messageId);
            message.Status.ShouldBe(MessageStatus.Pending);
            message.ExternalMessageId.ShouldBeNull();
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
        }
    }

    private static Attachment ExpiredAttachment(Guid tenantId, Guid uploaderId, string fileName) => new()
    {
        TenantId = tenantId,
        UploaderId = uploaderId,
        ObjectKey = $"cleanup/{Guid.NewGuid():N}/{fileName}",
        FileName = fileName,
        ContentType = "application/pdf",
        Size = 4,
        Status = AttachmentStatus.Staged,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Condition was not satisfied within {timeout.TotalSeconds:0}s.");
    }

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}
