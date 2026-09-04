using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

[CollectionDefinition("message-delivery-part")]
public sealed class MessageDeliveryPartCollection : ICollectionFixture<MessageDeliveryPartFixture>;

public sealed class MessageDeliveryPartFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string OwnerConnection => container.GetConnectionString();
    public string RuntimeConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(OwnerConnection);
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using var owner = Context(OwnerConnection);
        await owner.Database.MigrateAsync();
        RuntimeConnection = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
    public InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}

/// <summary>
/// Durable WhatsApp delivery parts: a requested send persists one part per provider message
/// (text/template/attachment), and the worker sends and reconciles each part idempotently,
/// aggregating part status onto the single parent timeline item.
/// </summary>
[Collection("message-delivery-part")]
public sealed class MessageDeliveryPartTests(MessageDeliveryPartFixture fixture)
{
    [DockerFact]
    public async Task Free_form_send_inside_window_creates_one_text_part()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        await RunInScope(seed.TenantId, seed.UserId, async (db, actor, token) =>
        {
            var inbox = Inbox(db, actor);
            var activity = await inbox.SendAsync(seed.ConversationId, new OutboundMessageCommand("hello", Guid.NewGuid().ToString("N")), token);

            var part = await db.MessageDeliveryParts.Where(x => x.MessageId == activity!.Id).OrderBy(x => x.Position).SingleAsync(token);
            part.Kind.ShouldBe(DeliveryPartKind.Text);
            part.Position.ShouldBe(0);
            part.Status.ShouldBe(MessageStatus.Pending);
            part.AttachmentId.ShouldBeNull();
            part.TemplateName.ShouldBeNull();
            (await db.MessageDeliveryParts.CountAsync(x => x.MessageId == activity!.Id, token)).ShouldBe(1);
        });
    }

    [DockerFact]
    public async Task Template_send_outside_window_creates_one_template_part()
    {
        var seed = await SeedConversationAsync(warmWindow: false);
        await RunInScope(seed.TenantId, seed.UserId, async (db, actor, token) =>
        {
            var inbox = Inbox(db, actor);
            var template = new OutboundTemplate("order_shipping", "en_US", new[] { JsonSerializer.Deserialize<JsonElement>("""{"type":"body","parameters":[{"type":"text","text":"order 42"}]}""") });
            var activity = await inbox.SendAsync(seed.ConversationId, new OutboundMessageCommand("", Guid.NewGuid().ToString("N"), Template: template), token);

            var part = await db.MessageDeliveryParts.Where(x => x.MessageId == activity!.Id).OrderBy(x => x.Position).SingleAsync(token);
            part.Kind.ShouldBe(DeliveryPartKind.Template);
            part.TemplateName.ShouldBe("order_shipping");
            part.TemplateLanguage.ShouldBe("en_US");
            part.TemplateComponentsJson.ShouldNotBeNullOrEmpty();
        });
    }

    [DockerFact]
    public async Task Closing_the_window_without_a_template_rejects_before_persistence()
    {
        var seed = await SeedConversationAsync(warmWindow: false);
        await RunInScope(seed.TenantId, seed.UserId, async (db, actor, token) =>
        {
            var inbox = Inbox(db, actor);
            var failure = await Should.ThrowAsync<InboxException>(() => inbox.SendAsync(seed.ConversationId, new OutboundMessageCommand("late reply", Guid.NewGuid().ToString("N")), token));
            failure.Code.ShouldBe("messaging_window_closed");
            (await db.Messages.IgnoreQueryFilters().AnyAsync(x => x.ConversationId == seed.ConversationId, token)).ShouldBeFalse();
        });
    }

    [DockerFact]
    public async Task Unapproved_template_outside_window_is_rejected_before_persistence()
    {
        var seed = await SeedConversationAsync(warmWindow: false);
        await RunInScope(seed.TenantId, seed.UserId, async (db, actor, token) =>
        {
            var inbox = Inbox(db, actor, new FakeTemplateService(approve: false));
            var failure = await Should.ThrowAsync<InboxException>(() => inbox.SendAsync(seed.ConversationId, new OutboundMessageCommand("", Guid.NewGuid().ToString("N"), Template: new OutboundTemplate("not_approved", "en_US")), token));
            failure.Code.ShouldBe("template_invalid");
        });
        await using var owner = fixture.Context(fixture.OwnerConnection);
        (await owner.Messages.IgnoreQueryFilters().AnyAsync(x => x.TenantId == seed.TenantId)).ShouldBeFalse();
        (await owner.MessageDeliveryParts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == seed.TenantId)).ShouldBeFalse();
    }

    [DockerFact]
    public async Task Body_plus_two_attachments_creates_three_ordered_parts()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var photo = Guid.NewGuid();
        var clip = Guid.NewGuid();
        await AddAttachmentAsync(seed, photo, "image/jpeg", "photo.jpg");
        await AddAttachmentAsync(seed, clip, "video/mp4", "clip.mp4");

        await RunInScope(seed.TenantId, seed.UserId, async (db, actor, token) =>
        {
            var inbox = Inbox(db, actor);
            var activity = await inbox.SendAsync(seed.ConversationId, new OutboundMessageCommand("here are the files", Guid.NewGuid().ToString("N"), new[] { photo, clip }), token);

            var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == activity!.Id).OrderBy(x => x.Position).ToListAsync(token);
            parts.Count.ShouldBe(3);
            parts[0].Kind.ShouldBe(DeliveryPartKind.Text);
            parts[0].AttachmentId.ShouldBeNull();
            parts[1].Kind.ShouldBe(DeliveryPartKind.Image);
            parts[1].AttachmentId.ShouldBe(photo);
            parts[2].Kind.ShouldBe(DeliveryPartKind.Video);
            parts[2].AttachmentId.ShouldBe(clip);
        });
    }

    [DockerFact]
    public async Task Parent_is_sent_only_after_every_part_succeeds()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Text, null), (DeliveryPartKind.Image, null)]);
        var sender = new RecordingSender();
        var outcome = await SendOnceAsync(seed.TenantId, messageId, sender);

        outcome.ShouldBe(OutboundOutcome.Sent);
        sender.Calls.ShouldBe(2);
        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var message = await db.Messages.SingleAsync(x => x.Id == messageId, token);
                message.Status.ShouldBe(MessageStatus.Sent);
                message.FailureReason.ShouldBeNull();
                var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == messageId).OrderBy(x => x.Position).ToListAsync(token);
                foreach (var part in parts)
                {
                    part.Status.ShouldBe(MessageStatus.Sent);
                    part.ExternalMessageId.ShouldNotBeNullOrWhiteSpace();
                }
            }, CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task Permanent_part_failure_fails_the_parent_message()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Text, null), (DeliveryPartKind.Image, null)]);
        var sender = new RecordingSender(null, new InvalidOperationException("provider rejected the media"));
        var outcome = await SendOnceAsync(seed.TenantId, messageId, sender);

        outcome.ShouldBe(OutboundOutcome.Failed);
        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var message = await db.Messages.SingleAsync(x => x.Id == messageId, token);
                message.Status.ShouldBe(MessageStatus.Failed);
                message.FailureReason.ShouldBe("provider_rejected");
                var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == messageId).OrderBy(x => x.Position).ToListAsync(token);
                parts[0].Status.ShouldBe(MessageStatus.Sent); // delivered before the failing part
                parts[1].Status.ShouldBe(MessageStatus.Failed);
                (await db.Notifications.IgnoreQueryFilters().AnyAsync(x => x.TenantId == seed.TenantId && x.Type == "message.failed", token)).ShouldBeTrue();
            }, CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task Transient_part_failure_retries_on_the_part_schedule_then_succeeds()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Image, null)]);
        var sender = new RecordingSender(new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable));

        (await SendOnceAsync(seed.TenantId, messageId, sender)).ShouldBe(OutboundOutcome.RetryScheduled);

        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var part = await db.MessageDeliveryParts.SingleAsync(x => x.MessageId == messageId, token);
                part.Status.ShouldBe(MessageStatus.Pending);
                part.Attempts.ShouldBe(1);
                part.NextAttemptAt.ShouldNotBeNull();
                part.ProviderRequestId.ShouldNotBeNullOrWhiteSpace();
                part.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); // sweep it again
                await db.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        (await SendOnceAsync(seed.TenantId, messageId, sender)).ShouldBe(OutboundOutcome.Sent);
        sender.Calls.ShouldBe(2);
        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var message = await db.Messages.SingleAsync(x => x.Id == messageId, token);
                message.Status.ShouldBe(MessageStatus.Sent);
                var part = await db.MessageDeliveryParts.SingleAsync(x => x.MessageId == messageId, token);
                part.Status.ShouldBe(MessageStatus.Sent);
                part.Attempts.ShouldBe(2);
            }, CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task Ambiguous_part_outcome_stops_resending_and_marks_unknown()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Text, null)]);
        var sender = new RecordingSender(new TaskCanceledException("timeout"));

        (await SendOnceAsync(seed.TenantId, messageId, sender)).ShouldBe(OutboundOutcome.Failed);

        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var part = await db.MessageDeliveryParts.SingleAsync(x => x.MessageId == messageId, token);
                part.Status.ShouldBe(MessageStatus.Unknown);
                part.ProviderRequestId.ShouldNotBeNullOrWhiteSpace();
                part.ExternalMessageId.ShouldBeNull();
                (await db.Messages.SingleAsync(x => x.Id == messageId, token)).Status.ShouldBe(MessageStatus.Unknown);
            }, CancellationToken.None);
        }

        // A re-drive must never resend an ambiguous part without a provider id.
        (await SendOnceAsync(seed.TenantId, messageId, sender)).ShouldBe(OutboundOutcome.Reconciled);
        sender.Calls.ShouldBe(1);
    }

    [DockerFact]
    public async Task Concurrent_redeliveries_send_a_part_exactly_once()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Text, null)]);
        var calls = 0;

        async Task Drive()
        {
            await using var db = fixture.Context(fixture.RuntimeConnection);
            var sender = new RecordingSender();
            sender.CallsChanged = () => Interlocked.Increment(ref calls);
            var processor = new MessageProcessor(db, sender, NullLogger<MessageProcessor>.Instance);
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, token => processor.SendOutboundAsync(messageId, token), CancellationToken.None);
        }

        await Task.WhenAll(Drive(), Drive());

        calls.ShouldBe(1);
        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var message = await db.Messages.SingleAsync(x => x.Id == messageId, token);
                message.Status.ShouldBe(MessageStatus.Sent);
                var part = await db.MessageDeliveryParts.SingleAsync(x => x.MessageId == messageId, token);
                part.Status.ShouldBe(MessageStatus.Sent);
                part.Attempts.ShouldBe(1);
            }, CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task Status_webhooks_resolve_delivery_part_ids()
    {
        var seed = await SeedConversationAsync(warmWindow: true);
        var messageId = await SeedOutboundAsync(seed, parts: [(DeliveryPartKind.Text, "wamid.part-1")]);
        await using (var owner = fixture.Context(fixture.OwnerConnection))
        {
            var part = await owner.MessageDeliveryParts.IgnoreQueryFilters().SingleAsync(x => x.MessageId == messageId);
            part.Status = MessageStatus.Sent;
            await owner.SaveChangesAsync();
        }
        var receipt = await AddReceiptAsync(seed, """{"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"phone-deliver"},"statuses":[{"id":"wamid.part-1","status":"delivered","timestamp":"1724000000"}]}}]}]}""");

        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            var sender = new RecordingSender();
            var processor = new MessageProcessor(db, sender, NullLogger<MessageProcessor>.Instance);
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, token => processor.NormalizeWebhookAsync(receipt, token), CancellationToken.None);
        }

        await using (var db = fixture.Context(fixture.RuntimeConnection))
        {
            await new TenantExecutionScope(db).RunAsync(seed.TenantId, async token =>
            {
                var part = await db.MessageDeliveryParts.SingleAsync(x => x.MessageId == messageId, token);
                part.Status.ShouldBe(MessageStatus.Delivered);
                (await db.Messages.SingleAsync(x => x.Id == messageId, token)).Status.ShouldBe(MessageStatus.Delivered);
            }, CancellationToken.None);
        }
    }

    private async Task<Seed> SeedConversationAsync(bool warmWindow)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var owner = fixture.Context(fixture.OwnerConnection);
        owner.Tenants.Add(new Tenant(seed.TenantId, "mdp-" + seed.TenantId.ToString("N")[..6], "MDP"));
        owner.Users.Add(new User(seed.UserId, seed.TenantId, "agent@example.com", "Agent", UserRole.Agent) { NormalizedEmail = "AGENT@EXAMPLE.COM", EmailVerifiedAt = DateTimeOffset.UtcNow, PasswordHash = "x" });
        owner.Channels.Add(new Channel(seed.ChannelId, seed.TenantId, "whatsapp", "phone-deliver", true));
        owner.Contacts.Add(new Contact(seed.ContactId, seed.TenantId, "whatsapp", "phone-deliver", "15550001", "Customer", "+15550001"));
        owner.Conversations.Add(new Conversation { Id = seed.ConversationId, TenantId = seed.TenantId, ChannelId = seed.ChannelId, ContactId = seed.ContactId, ExternalConversationId = "15550001", LastCustomerMessageAt = warmWindow ? DateTimeOffset.UtcNow : null });
        await owner.SaveChangesAsync();
        return seed;
    }

    private async Task AddAttachmentAsync(Seed seed, Guid id, string contentType, string fileName)
    {
        await using var owner = fixture.Context(fixture.OwnerConnection);
        owner.Attachments.Add(new Attachment
        {
            Id = id,
            TenantId = seed.TenantId,
            UploaderId = seed.UserId,
            ObjectKey = $"obj/{seed.TenantId:N}/{id:N}/{fileName}",
            FileName = fileName,
            ContentType = contentType,
            Size = 1,
            Status = AttachmentStatus.Ready,
            CompletedAt = DateTimeOffset.UtcNow,
            DetectedContentType = contentType,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        await owner.SaveChangesAsync();
    }

    private async Task<Guid> SeedOutboundAsync(Seed seed, params (DeliveryPartKind Kind, string? ExternalMessageId)[] parts)
    {
        var messageId = Guid.NewGuid();
        await using var owner = fixture.Context(fixture.OwnerConnection);
        owner.Messages.Add(new Message
        {
            Id = messageId,
            TenantId = seed.TenantId,
            ChannelId = seed.ChannelId,
            ConversationId = seed.ConversationId,
            Direction = MessageDirection.Outbound,
            SenderUserId = seed.UserId,
            Body = "seeded outbound",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = MessageStatus.Pending,
            Sequence = 1,
        });
        owner.MessageDeliveryParts.AddRange(parts.Select((spec, index) => new MessageDeliveryPart
        {
            TenantId = seed.TenantId,
            MessageId = messageId,
            Position = index,
            Kind = spec.Kind,
            ExternalMessageId = spec.ExternalMessageId,
            Status = MessageStatus.Pending,
        }));
        await owner.SaveChangesAsync();
        return messageId;
    }

    private async Task<Guid> AddReceiptAsync(Seed seed, string body)
    {
        await using var owner = fixture.Context(fixture.OwnerConnection);
        var receipt = new global::UnifiedInbox.Domain.WebhookReceipt { TenantId = seed.TenantId, ChannelId = seed.ChannelId, ProviderEventId = Guid.NewGuid().ToString("N"), RawBody = Encoding.UTF8.GetBytes(body) };
        owner.WebhookReceipts.Add(receipt);
        await owner.SaveChangesAsync();
        return receipt.Id;
    }

    private async Task<OutboundOutcome> SendOnceAsync(Guid tenantId, Guid messageId, RecordingSender sender)
    {
        await using var db = fixture.Context(fixture.RuntimeConnection);
        var processor = new MessageProcessor(db, sender, NullLogger<MessageProcessor>.Instance);
        return await new TenantExecutionScope(db).RunAsync(tenantId, token => processor.SendOutboundAsync(messageId, token), CancellationToken.None);
    }

    private async Task RunInScope(Guid tenantId, Guid userId, Func<InboxDbContext, TestActor, CancellationToken, Task> body)
    {
        await using var db = fixture.Context(fixture.RuntimeConnection);
        var actor = new TestActor(tenantId, userId);
        await new TenantExecutionScope(db).RunAsync(tenantId, token => body(db, actor, token), CancellationToken.None);
    }

    private static PersistentInboxService Inbox(InboxDbContext db, TestActor actor, IWhatsAppTemplateService? templates = null) =>
        new(db, actor, new AttachmentService(db, actor, new UnusedStorage(), new UnusedScanner(), new TestEnvironment()), templates ?? new FakeTemplateService());

    private sealed record Seed(Guid TenantId, Guid UserId, Guid ChannelId, Guid ContactId, Guid ConversationId);

    private sealed record TestActor(Guid TenantId, Guid UserId) : ICurrentTenant
    {
        Guid? ICurrentTenant.TenantId => TenantId;
        Guid? ICurrentTenant.UserId => UserId;
        public UserRole? Role => UserRole.Agent;
    }

    private sealed class RecordingSender(params Exception?[] failures) : WhatsAppMessageSender(new HttpClient(), new DictionaryConfiguration(new Dictionary<string, string?> { ["WhatsApp:UseFake"] = "true" }), new TestEnvironment())
    {
        private readonly Queue<Exception?> script = new(failures);
        public int Calls { get; private set; }
        public Action? CallsChanged { get; set; }

        public override Task<string> SendPartAsync(InboxDbContext db, Channel channel, Contact contact, string body, MessageDeliveryPart part, CancellationToken token)
        {
            Calls++;
            CallsChanged?.Invoke();
            if (script.TryDequeue(out var failure) && failure is not null) throw failure;
            return Task.FromResult($"wamid.{Guid.NewGuid():N}");
        }
    }

    private sealed class FakeTemplateService(bool approve = true) : IWhatsAppTemplateService
    {
        public Task<IReadOnlyList<WhatsAppTemplateInfo>> ApprovedAsync(Guid channelId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WhatsAppTemplateInfo>>([]);
        public Task ValidateAsync(Guid channelId, OutboundTemplate template, CancellationToken cancellationToken) =>
            approve ? Task.CompletedTask : throw new InboxException("template_invalid", "The template is not approved.", 422);
    }

    private sealed class TestEnvironment : IHostEnvironment    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class UnusedStorage : IObjectStorage
    {
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StoreAsync(string objectKey, string contentType, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedScanner : IAttachmentScanner
    {
        public bool IsConfigured => true;
        public Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
