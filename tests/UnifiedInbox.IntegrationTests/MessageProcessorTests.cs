using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

public sealed class MessageProcessorTests
{
    private const string InboundBody = """{"entry":[{"changes":[{"value":{"messages":[{"id":"wamid.1","from":"15550001","text":{"body":"hello"}}]}}]}]}""";

    [Fact]
    public async Task Duplicate_webhook_delivery_yields_one_message()
    {
        var seed = Seed();
        var receipt = seed.AddReceipt(InboundBody);
        var processor = seed.Processor(new ScriptedSender());

        (await processor.NormalizeWebhookAsync(receipt.Id, CancellationToken.None)).ShouldBe(WebhookOutcome.Processed);
        (await processor.NormalizeWebhookAsync(receipt.Id, CancellationToken.None)).ShouldBe(WebhookOutcome.Ignored);

        seed.Db.Messages.IgnoreQueryFilters().Count().ShouldBe(1);
        seed.OutboxTypes().ShouldContain("conversation.created");
        seed.OutboxTypes().ShouldContain("message.created");
        seed.OutboxTypes().ShouldContain("conversation.updated");
        seed.OutboxTypes().ShouldContain("channel.updated");
    }

    [Fact]
    public async Task Transient_provider_failure_schedules_a_retry_then_succeeds()
    {
        var seed = Seed();
        var message = seed.AddOutbound("hello");
        var sender = new ScriptedSender(new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));
        var processor = seed.Processor(sender);

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.RetryScheduled);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Pending);
        seed.Reload(message).Attempts.ShouldBe(1);
        seed.Reload(message).NextAttemptAt.ShouldNotBeNull();

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Sent);
        var sent = seed.Reload(message);
        sent.Status.ShouldBe(MessageStatus.Sent);
        sent.ExternalMessageId.ShouldStartWith("fake-");
        sender.Calls.ShouldBe(2);
        seed.OutboxTypes().ShouldContain("message.statusChanged");
    }

    [Fact]
    public async Task Permanent_provider_failure_dead_letters_with_admin_notification()
    {
        var seed = Seed();
        var message = seed.AddOutbound("boom [permanent-failure]");
        var processor = seed.Processor(new ScriptedSender(new InvalidOperationException("rejected")));

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Failed);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Failed);
        seed.Db.Notifications.IgnoreQueryFilters().ShouldHaveSingleItem().Type.ShouldBe("message.failed");
        seed.OutboxTypes().ShouldContain("notification.created");
    }

    [Fact]
    public async Task Ambiguous_timeout_reconciles_instead_of_resending()
    {
        var seed = Seed();
        var message = seed.AddOutbound("hello?");
        var sender = new ScriptedSender(new TaskCanceledException("timeout"));
        var processor = seed.Processor(sender);

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Failed);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Unknown);

        // Re-drives must not resend: without a provider id the message waits for review.
        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Reconciled);
        sender.Calls.ShouldBe(1);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Unknown);
        seed.Db.Notifications.IgnoreQueryFilters().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Interrupted_send_with_provider_id_reconciles_to_sent()
    {
        var seed = Seed();
        var message = seed.AddOutbound("hello?");
        message.Status = MessageStatus.Sending;
        message.ExternalMessageId = "wamid.provider-1";
        message.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        seed.Db.SaveChanges();
        var sender = new ScriptedSender();
        var processor = seed.Processor(sender);

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Reconciled);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Sent);
        sender.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Closed_messaging_window_fails_without_touching_the_provider()
    {
        var seed = Seed(warmWindow: false);
        var message = seed.AddOutbound("late hello");
        var sender = new ScriptedSender();
        var processor = seed.Processor(sender);

        (await processor.SendOutboundAsync(message.Id, CancellationToken.None)).ShouldBe(OutboundOutcome.Failed);
        seed.Reload(message).Status.ShouldBe(MessageStatus.Failed);
        seed.Reload(message).FailureReason.ShouldBe("template_required");
        sender.Calls.ShouldBe(0);
    }

    [Fact]
    public void Envelope_ids_parse_from_new_and_legacy_payloads()
    {
        var id = Guid.NewGuid();
        MessagingConsumerExtract("{\"messageId\":\"" + id + "\"}").ShouldBe(id);
        MessagingConsumerExtract("{\"receiptId\":\"" + id + "\"}").ShouldBe(id);
        MessagingConsumerExtract("{\"id\":\"" + id + "\"}").ShouldBe(id);
        MessagingConsumerExtract("{not json").ShouldBeNull();
        MessagingConsumerExtract("{}").ShouldBeNull();
    }

    private static Guid? MessagingConsumerExtract(string json) =>
        MessageEnvelope.ExtractId(Encoding.UTF8.GetBytes(json));

    private static SeedData Seed(bool warmWindow = true)
    {
        var tenantId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, Guid.NewGuid(), UserRole.Owner);
        var channel = new Channel(Guid.NewGuid(), tenantId, "whatsapp", "phone-123", true) { DisplayName = "Sales" };
        db.Channels.Add(channel);
        var contact = new Contact(Guid.NewGuid(), tenantId, "whatsapp", "phone-123", "15550001", "Customer", "+15550001");
        db.Contacts.Add(contact);
        db.SaveChanges();
        return new(db, tenantId, channel, contact, warmWindow);
    }

    private sealed record SeedData(InboxDbContext Db, Guid TenantId, Channel Channel, Contact Contact, bool WarmWindow)
    {
        public WebhookReceipt AddReceipt(string body)
        {
            var receipt = new WebhookReceipt { TenantId = TenantId, ChannelId = Channel.Id, ProviderEventId = Guid.NewGuid().ToString(), RawBody = Encoding.UTF8.GetBytes(body) };
            Db.WebhookReceipts.Add(receipt);
            Db.SaveChanges();
            return receipt;
        }

        public Message AddOutbound(string body)
        {
            var conversation = new Conversation
            {
                TenantId = TenantId,
                ChannelId = Channel.Id,
                ContactId = Contact.Id,
                ExternalConversationId = "15550001",
                LastCustomerMessageAt = WarmWindow ? DateTimeOffset.UtcNow : null,
            };
            Db.Conversations.Add(conversation);
            var message = new Message
            {
                TenantId = TenantId,
                ChannelId = Channel.Id,
                ConversationId = conversation.Id,
                Direction = MessageDirection.Outbound,
                Body = body,
                IdempotencyKey = Guid.NewGuid().ToString(),
                Status = MessageStatus.Pending,
                Sequence = 1,
            };
            Db.Messages.Add(message);
            Db.SaveChanges();
            return message;
        }

        public MessageProcessor Processor(WhatsAppMessageSender sender) =>
            new(Db, sender, NullLogger<MessageProcessor>.Instance);

        public Message Reload(Message message)
        {
            Db.Entry(message).Reload();
            return message;
        }

        public List<string> OutboxTypes() => Db.Outbox.IgnoreQueryFilters().Select(x => x.Type).ToList();
    }

    private sealed class ScriptedSender(params Exception?[] failures) : WhatsAppMessageSender(new HttpClient(), new DictionaryConfiguration([]), new TestEnvironment())
    {
        private readonly Queue<Exception?> script = new(failures);
        public int Calls { get; private set; }
        public override Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token)
        {
            Calls++;
            if (script.TryDequeue(out var failure) && failure is not null) throw failure;
            return Task.FromResult($"fake-{Guid.NewGuid():N}");
        }
    }
}
