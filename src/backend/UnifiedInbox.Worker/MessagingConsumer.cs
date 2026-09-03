using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.Worker;

public sealed class MessagingConsumer(IServiceScopeFactory scopes, ConnectionFactory factory, WhatsAppMessageSender sender, ILogger<MessagingConsumer> logger) : BackgroundService
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
            try { await Handle(delivery.BasicProperties.Type, delivery.Body.ToArray(), token); await channel.BasicAckAsync(delivery.DeliveryTag, false, token); }
            catch (Exception exception) { logger.LogError(exception, "Messaging job {MessageId} failed", delivery.BasicProperties.MessageId); await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: delivery.Redelivered is false, token); }
        };
        await channel.BasicConsumeAsync("unified-inbox.worker", autoAck: false, consumer, token);
        await Task.Delay(Timeout.Infinite, token);
    }

    private async Task Handle(string? type, byte[] payload, CancellationToken token)
    {
        using var json = JsonDocument.Parse(payload); var id = json.RootElement.EnumerateObject().First().Value.GetGuid();
        if (type == "webhook.received") await NormalizeWebhook(id, token);
        else if (type == "outbound.message.requested") await SendOutbound(id, token);
    }

    private async Task NormalizeWebhook(Guid receiptId, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
        var receipt = await db.WebhookReceipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == receiptId, token); if (receipt is null || receipt.Status is WebhookStatus.Processed or WebhookStatus.Ignored) return;
        receipt.Status = WebhookStatus.Processing; await db.SaveChangesAsync(token);
        var channel = await db.Channels.IgnoreQueryFilters().SingleAsync(x => x.Id == receipt.ChannelId, token); using var document = JsonDocument.Parse(receipt.RawBody); var inputs = new WhatsAppPayloadParser().Parse(document.RootElement);
        foreach (var input in inputs)
        {
            if (await db.Messages.IgnoreQueryFilters().AnyAsync(x => x.ChannelId == channel.Id && x.ExternalMessageId == input.ExternalMessageId, token)) continue;
            var contact = await db.Contacts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == channel.TenantId && x.Platform == channel.Platform && x.ExternalAccountId == channel.ExternalAccountId && x.ExternalCustomerId == input.CustomerId, token);
            if (contact is null) { contact = new Contact(Guid.NewGuid(), channel.TenantId, channel.Platform, channel.ExternalAccountId, input.CustomerId, input.CustomerId, input.CustomerId); db.Contacts.Add(contact); }
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == channel.TenantId && x.ChannelId == channel.Id && x.ExternalConversationId == input.CustomerId, token);
            if (conversation is null) { conversation = new Conversation { TenantId = channel.TenantId, ChannelId = channel.Id, ContactId = contact.Id, ExternalConversationId = input.CustomerId }; db.Conversations.Add(conversation); }
            var sequence = Math.Max(await db.Messages.IgnoreQueryFilters().Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0, await db.InternalNotes.IgnoreQueryFilters().Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0) + 1;
            var message = new Message { TenantId = channel.TenantId, ChannelId = channel.Id, ConversationId = conversation.Id, Direction = MessageDirection.Inbound, Body = input.Text ?? $"[{input.MediaMimeType ?? "unsupported message"}]", ExternalMessageId = input.ExternalMessageId, Status = MessageStatus.Delivered, Sequence = sequence };
            conversation.RecordInboundActivity(message.CreatedAt); db.Messages.Add(message); db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "message.created", JsonSerializer.Serialize(new { id = message.Id, conversationId = conversation.Id }), DateTimeOffset.UtcNow));
        }
        receipt.Status = inputs.Count == 0 ? WebhookStatus.Ignored : WebhookStatus.Processed; await db.SaveChangesAsync(token);
    }

    private async Task SendOutbound(Guid messageId, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
        var message = await db.Messages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == messageId, token); if (message is null || message.Status is not MessageStatus.Pending and not MessageStatus.Unknown) return;
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(x => x.Id == message.ConversationId, token); var channel = await db.Channels.IgnoreQueryFilters().SingleAsync(x => x.Id == message.ChannelId, token); var contact = await db.Contacts.IgnoreQueryFilters().SingleAsync(x => x.Id == conversation.ContactId, token);
        if (new WhatsAppMessagingPolicy().Evaluate(conversation.LastCustomerMessageAt, DateTimeOffset.UtcNow, hasApprovedTemplate: false) == WhatsAppSendDecision.TemplateRequired) { message.Status = MessageStatus.Failed; message.FailureReason = "template_required"; }
        else { message.Status = MessageStatus.Sending; await db.SaveChangesAsync(token); var providerId = await sender.SendAsync(db, channel, contact, message.Body, token); message.ExternalMessageId = providerId; message.Status = MessageStatus.Sent; channel.LastOutboundAt = DateTimeOffset.UtcNow; }
        db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), message.TenantId, "message.statusChanged", JsonSerializer.Serialize(new { id = message.Id, conversationId = message.ConversationId, status = message.Status.ToString() }), DateTimeOffset.UtcNow)); await db.SaveChangesAsync(token);
    }
}

public sealed class WhatsAppMessageSender(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
{
    public async Task<string> SendAsync(InboxDbContext db, Channel channel, Contact contact, string body, CancellationToken token)
    {
        var fake = configuration.GetValue("WhatsApp:UseFake", environment.IsDevelopment() || environment.IsEnvironment("Test"));
        if (fake)
        {
            if (body.Contains("[rate-limit]", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("Simulated rate limit.", null, System.Net.HttpStatusCode.TooManyRequests);
            if (body.Contains("[permanent-failure]", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Simulated permanent provider rejection.");
            return $"fake-{Guid.NewGuid():N}";
        }
        var credential = await db.ChannelCredentials.IgnoreQueryFilters().SingleAsync(x => x.ChannelId == channel.Id, token);
        var key = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? throw new InvalidOperationException("Credentials:MasterKey is required.")); var accessToken = new CredentialProtector(key).Unprotect(credential.EncryptedAccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{configuration["WhatsApp:GraphVersion"] ?? "v23.0"}/{channel.ExternalAccountId}/messages"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); request.Content = JsonContent.Create(new { messaging_product = "whatsapp", to = contact.Phone, type = "text", text = new { body } });
        using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode(); using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token)); return result.RootElement.GetProperty("messages")[0].GetProperty("id").GetString() ?? throw new InvalidOperationException("Provider did not return a message id.");
    }
}
