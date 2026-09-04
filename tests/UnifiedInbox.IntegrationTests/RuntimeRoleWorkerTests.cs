using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using Shouldly;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;
using UnifiedInbox.Worker;

namespace UnifiedInbox.IntegrationTests;

public sealed class RuntimeRoleWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private string runtimeConnection = "";
    private IConnection rabbitConnection = null!;
    private IChannel rabbitChannel = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(container.StartAsync(), rabbit.StartAsync());
        await using var admin = new NpgsqlConnection(container.GetConnectionString()); await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using var owner = Context(container.GetConnectionString()); await owner.Database.MigrateAsync();
        runtimeConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Username = "app_runtime", Password = "test-only" }.ConnectionString;
        rabbitConnection = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync();
        rabbitChannel = await rabbitConnection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await rabbitChannel.ExchangeDeclareAsync("unified-inbox.events", ExchangeType.Topic, durable: true, autoDelete: false);
        await rabbitChannel.QueueDeclareAsync("runtime-role-test", durable: false, exclusive: true, autoDelete: true);
        await rabbitChannel.QueueBindAsync("runtime-role-test", "unified-inbox.events", "message.created");
    }

    public async Task DisposeAsync()
    {
        if (rabbitChannel is not null) await rabbitChannel.DisposeAsync();
        if (rabbitConnection is not null) await rabbitConnection.DisposeAsync();
        await Task.WhenAll(container.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask());
    }

    [DockerFact]
    public async Task Runtime_workers_dispatch_consume_cleanup_and_monitor_inside_tenant_scope()
    {
        var tenantA = Guid.NewGuid(); var tenantB = Guid.NewGuid(); var channelA = Guid.NewGuid(); var channelB = Guid.NewGuid();
        var userA = Guid.NewGuid(); var contactA = Guid.NewGuid(); var conversationA = Guid.NewGuid(); var messageA = Guid.NewGuid();
        var outboxA = Guid.NewGuid(); var attachmentA = Guid.NewGuid();
        await using (var owner = Context(container.GetConnectionString()))
        {
            owner.Tenants.AddRange(new Tenant(tenantA, "worker-a", "A"), new Tenant(tenantB, "worker-b", "B"));
            owner.Users.Add(new User(userA, tenantA, "owner@a.test", "Owner", UserRole.Owner) { NormalizedEmail = "OWNER@A.TEST", PasswordHash = "unused" });
            owner.Channels.AddRange(
                new Channel(channelA, tenantA, "whatsapp", "worker-phone-a", true) { Status = "connected", LastWebhookAt = DateTimeOffset.UtcNow.AddDays(-2) },
                new Channel(channelB, tenantB, "whatsapp", "worker-phone-b", true) { Status = "connected", LastWebhookAt = DateTimeOffset.UtcNow.AddDays(-2) });
            owner.Contacts.Add(new Contact(contactA, tenantA, "whatsapp", "worker-phone-a", "15550001", "Customer", "+15550001"));
            owner.Conversations.Add(new Conversation { Id = conversationA, TenantId = tenantA, ChannelId = channelA, ContactId = contactA, ExternalConversationId = "15550001", LastCustomerMessageAt = DateTimeOffset.UtcNow });
            owner.Messages.Add(new Message { Id = messageA, TenantId = tenantA, ChannelId = channelA, ConversationId = conversationA, Direction = MessageDirection.Outbound, Body = "hello", Status = MessageStatus.Pending, Sequence = 1 });
            owner.Outbox.Add(new OutboxEvent(outboxA, tenantA, "message.created", "{\"id\":\"" + messageA + "\"}", DateTimeOffset.UtcNow));
            owner.Attachments.Add(new Attachment { Id = attachmentA, TenantId = tenantA, UploaderId = userA, ObjectKey = "expired/object", FileName = "old.pdf", ContentType = "application/pdf", Size = 4, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await owner.SaveChangesAsync();
        }

        var signer = new TenantHeaderSigner("test-process-secret");
        signer.TryRead(signer.Create(tenantA), out var routedTenant).ShouldBeTrue(); routedTenant.ShouldBe(tenantA);
        var tampered = signer.Create(tenantA); tampered["tenant-id"] = tenantB.ToString(); signer.TryRead(tampered, out _).ShouldBeFalse();

        await using var db = Context(runtimeConnection); var scope = new TenantExecutionScope(db);

        await scope.RunAsync(tenantA, token => OutboxDispatcher.DispatchTenantBatchAsync(rabbitChannel, db, tenantA, signer, NullLogger.Instance, token), CancellationToken.None);
        var delivery = await rabbitChannel.BasicGetAsync("runtime-role-test", autoAck: true);
        delivery.ShouldNotBeNull();
        signer.TryRead(delivery!.BasicProperties.Headers, out var dispatchedTenant).ShouldBeTrue();
        dispatchedTenant.ShouldBe(tenantA);
        await scope.RunAsync(tenantA, async token => (await db.Outbox.SingleAsync(x => x.Id == outboxA, token)).Status.ShouldBe(OutboxStatus.Processed), CancellationToken.None);

        var processor = new MessageProcessor(db, new ScriptedSender(), NullLogger<MessageProcessor>.Instance);
        await MessagingConsumer.ProcessDeliveryAsync("outbound.message.requested", messageA, signer.Create(tenantA), signer, db, scope, processor, NullLogger.Instance, CancellationToken.None);
        await scope.RunAsync(tenantA, async token => (await db.Messages.SingleAsync(x => x.Id == messageA, token)).Status.ShouldBe(MessageStatus.Sent), CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(() => MessagingConsumer.ProcessDeliveryAsync("outbound.message.requested", messageA, signer.Create(tenantB), signer, db, scope, processor, NullLogger.Instance, CancellationToken.None));

        var storage = new FakeStorage(); storage.Objects.Add("expired/object");
        var attachmentService = new AttachmentService(db, new TestTenant(tenantA, userA), storage, new FakeScanner(), new TestEnvironment());
        (await AttachmentCleanupWorker.CleanupTenantAsync(attachmentService, scope, tenantA, CancellationToken.None)).ShouldBe(1);
        storage.Objects.ShouldBeEmpty();

        await scope.RunAsync(tenantA, token => ChannelHealthMonitor.MonitorTenantAsync(db, token), CancellationToken.None);
        await scope.RunAsync(tenantA, async token =>
        {
            (await db.ChannelHealth.CountAsync(token)).ShouldBe(1);
            (await db.Notifications.CountAsync(token)).ShouldBe(1);
            (await db.Outbox.AnyAsync(x => x.Type == "channel.updated", token)).ShouldBeTrue();
            (await db.Outbox.AnyAsync(x => x.Type == "notification.created", token)).ShouldBeTrue();
        }, CancellationToken.None);
        await scope.RunAsync(tenantB, async token => (await db.ChannelHealth.CountAsync(token)).ShouldBe(0), CancellationToken.None);
    }

    private sealed class ScriptedSender : WhatsAppMessageSender
    {
        public ScriptedSender() : base(new HttpClient(), new DictionaryConfiguration([]), new TestEnvironment()) { }
        public override Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token) => Task.FromResult("wamid.runtime-role");
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public HashSet<string> Objects { get; } = [];
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Objects.Remove(objectKey); return Task.CompletedTask; }
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeScanner : IAttachmentScanner
    {
        public bool IsConfigured => true;
        public Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}
