using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Messaging;

public enum WebhookOutcome { Processed, Ignored, RetryScheduled, DeadLettered }
public enum OutboundOutcome { Sent, RetryScheduled, Reconciled, Failed, Ignored }

/// <summary>Parses broker payloads, accepting both explicit keys and the legacy id-first shape.</summary>
public static class MessageEnvelope
{
    public static Guid? ExtractId(byte[] payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            foreach (var name in new[] { "receiptId", "messageId", "id" })
                if (json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed))
                    return parsed;
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String && Guid.TryParse(property.Value.GetString(), out var fallback))
                    return fallback;
            return null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Durable messaging core: idempotent webhook normalization and provider sends with
/// scheduled retries, dead-lettering, and ambiguous-send reconciliation. All state lives
/// in the database so any worker instance (or restart) can resume without loss or dupes.
/// </summary>
public sealed class MessageProcessor(InboxDbContext db, WhatsAppMessageSender sender, ILogger<MessageProcessor> logger)
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    public async Task<WebhookOutcome> NormalizeWebhookAsync(Guid receiptId, CancellationToken token)
    {
        var receipt = await db.WebhookReceipts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == receiptId, token);
        if (receipt is null || receipt.Status is WebhookStatus.Processed or WebhookStatus.Ignored) return WebhookOutcome.Ignored;
        receipt.Attempts++;
        receipt.Status = WebhookStatus.Processing;
        receipt.AvailableAt = DateTimeOffset.UtcNow.Add(VisibilityTimeout);
        receipt.LastError = null;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return WebhookOutcome.Ignored; } // another worker claimed it
        try
        {
            var parsed = new WhatsAppPayloadParser().ParseFull(JsonDocument.Parse(receipt.RawBody).RootElement);
            if (parsed.Messages.Count == 0 && parsed.Statuses.Count == 0)
            {
                receipt.Status = WebhookStatus.Ignored;
                await db.SaveChangesAsync(token);
                return WebhookOutcome.Ignored;
            }
            var channel = await db.Channels.IgnoreQueryFilters().SingleAsync(x => x.Id == receipt.ChannelId, token);
            foreach (var input in parsed.Messages) await PersistInboundAsync(channel, input, token);
            foreach (var update in parsed.Statuses) await ApplyStatusUpdateAsync(channel, update, token);
            channel.LastWebhookAt = DateTimeOffset.UtcNow;
            Emit(channel.TenantId, "channel.updated", channel.Id);
            receipt.Status = WebhookStatus.Processed;
            receipt.LastError = null;
            await db.SaveChangesAsync(token);
            return WebhookOutcome.Processed;
        }
        catch (Exception exception) when (OutboxRetryPolicy.IsTransient(exception) && receipt.Attempts < OutboxRetryPolicy.MaxAttempts)
        {
            logger.LogWarning(exception, "Webhook {ReceiptId} normalization failed transiently (attempt {Attempt})", receiptId, receipt.Attempts);
            receipt.LastError = exception.GetType().Name;
            receipt.AvailableAt = DateTimeOffset.UtcNow.Add(OutboxRetryPolicy.NextDelay(receipt.Attempts));
            await db.SaveChangesAsync(token);
            return WebhookOutcome.RetryScheduled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Webhook {ReceiptId} normalization dead-lettered", receiptId);
            receipt.Status = WebhookStatus.Failed;
            receipt.LastError = exception.GetType().Name + ": " + exception.Message;
            await db.SaveChangesAsync(token);
            await NotifyAdminsAsync(receipt.TenantId, "webhook.failed", $"An incoming message could not be processed ({receipt.LastError}).", token);
            return WebhookOutcome.DeadLettered;
        }
    }

    public async Task<OutboundOutcome> SendOutboundAsync(Guid messageId, CancellationToken token)
    {
        var message = await db.Messages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == messageId, token);
        if (message is null || message.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed) return OutboundOutcome.Ignored;
        if (message.Status == MessageStatus.Unknown) return await ReconcileAsync(message, notify: false, token);
        if (message.Status == MessageStatus.Sending)
        {
            // Still inside another worker's visibility timeout: leave it alone.
            if (message.NextAttemptAt > DateTimeOffset.UtcNow) return OutboundOutcome.Ignored;
            // A previous attempt never reported back (crash or lost response): reconcile, never blindly resend.
            return await ReconcileAsync(message, notify: true, token);
        }
        // Pending rows are always claimed atomically below, so broker redeliveries and
        // sweeper replays collapse into a single send via the row-version claim.

        var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(x => x.Id == message.ConversationId, token);
        var channel = await db.Channels.IgnoreQueryFilters().SingleAsync(x => x.Id == message.ChannelId, token);
        var contact = await db.Contacts.IgnoreQueryFilters().SingleAsync(x => x.Id == conversation.ContactId, token);
        if (!channel.IsEnabled)
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = "channel_disabled";
            message.NextAttemptAt = null;
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            return OutboundOutcome.Failed;
        }
        if (new WhatsAppMessagingPolicy().Evaluate(conversation.LastCustomerMessageAt, DateTimeOffset.UtcNow, hasApprovedTemplate: false) == WhatsAppSendDecision.TemplateRequired)
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = "template_required";
            message.NextAttemptAt = null;
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            return OutboundOutcome.Failed;
        }

        message.Status = MessageStatus.Sending;
        message.Attempts++;
        message.NextAttemptAt = DateTimeOffset.UtcNow.Add(VisibilityTimeout);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return OutboundOutcome.Ignored; } // another worker claimed it
        try
        {
            var providerId = await sender.SendAsync(db, channel, contact, message.Body, token);
            message.ExternalMessageId = providerId;
            message.Status = MessageStatus.Sent;
            message.FailureReason = null;
            message.NextAttemptAt = null;
            channel.LastOutboundAt = DateTimeOffset.UtcNow;
            Emit(message.TenantId, "message.statusChanged", message.Id);
            Emit(message.TenantId, "channel.updated", channel.Id);
            await db.SaveChangesAsync(token);
            return OutboundOutcome.Sent;
        }
        catch (Exception exception) when (OutboxRetryPolicy.IsAmbiguous(exception))
        {
            // The provider may already have the message: stop retrying, reconcile on review.
            logger.LogWarning(exception, "Outbound message {MessageId} has an ambiguous outcome; reconciling instead of resending", messageId);
            message.Status = MessageStatus.Unknown;
            message.FailureReason = "ambiguous_provider_outcome";
            message.NextAttemptAt = null;
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            await NotifyAdminsAsync(message.TenantId, "message.failed", "A message may or may not have been delivered and needs review.", token);
            return OutboundOutcome.Failed;
        }
        catch (Exception exception) when (OutboxRetryPolicy.IsTransient(exception) && message.Attempts < OutboxRetryPolicy.MaxAttempts)
        {
            logger.LogWarning(exception, "Outbound message {MessageId} failed transiently (attempt {Attempt})", messageId, message.Attempts);
            message.Status = MessageStatus.Pending;
            message.FailureReason = exception.GetType().Name;
            message.NextAttemptAt = DateTimeOffset.UtcNow.Add(OutboxRetryPolicy.NextDelay(message.Attempts));
            await db.SaveChangesAsync(token);
            return OutboundOutcome.RetryScheduled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Outbound message {MessageId} dead-lettered", messageId);
            message.Status = MessageStatus.Failed;
            message.FailureReason = exception.GetType().Name + ": " + exception.Message;
            message.NextAttemptAt = null;
            db.ChannelHealth.Add(new ChannelHealth { TenantId = message.TenantId, ChannelId = message.ChannelId, IsHealthy = false, Reason = "repeated_send_failures" });
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            await NotifyAdminsAsync(message.TenantId, "message.failed", $"A message could not be delivered ({message.FailureReason}).", token);
            return OutboundOutcome.Failed;
        }
    }

    /// <summary>Reconciles an ambiguous send by provider request id instead of resending.</summary>
    private async Task<OutboundOutcome> ReconcileAsync(Message message, bool notify, CancellationToken token)
    {
        if (message.ExternalMessageId is not null)
        {
            message.Status = MessageStatus.Sent;
            message.NextAttemptAt = null;
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            return OutboundOutcome.Reconciled;
        }
        if (notify && message.FailureReason != "ambiguous_provider_outcome")
        {
            message.FailureReason = "ambiguous_provider_outcome";
            Emit(message.TenantId, "message.statusChanged", message.Id);
            await db.SaveChangesAsync(token);
            await NotifyAdminsAsync(message.TenantId, "message.failed", "A message send was interrupted and needs review before retrying.", token);
        }
        return OutboundOutcome.Reconciled;
    }

    private async Task PersistInboundAsync(Channel channel, WhatsAppInbound input, CancellationToken token)
    {
        if (await db.Messages.IgnoreQueryFilters().AnyAsync(x => x.ChannelId == channel.Id && x.ExternalMessageId == input.ExternalMessageId, token)) return;
        var contact = await db.Contacts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == channel.TenantId && x.Platform == channel.Platform && x.ExternalAccountId == channel.ExternalAccountId && x.ExternalCustomerId == input.CustomerId, token);
        if (contact is null) { contact = new Contact(Guid.NewGuid(), channel.TenantId, channel.Platform, channel.ExternalAccountId, input.CustomerId, input.CustomerId, input.CustomerId); db.Contacts.Add(contact); }
        var conversation = await db.Conversations.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == channel.TenantId && x.ChannelId == channel.Id && x.ExternalConversationId == input.CustomerId, token);
        if (conversation is null)
        {
            conversation = new Conversation { TenantId = channel.TenantId, ChannelId = channel.Id, ContactId = contact.Id, ExternalConversationId = input.CustomerId };
            db.Conversations.Add(conversation);
            Emit(channel.TenantId, "conversation.created", conversation.Id);
        }
        var sequence = Math.Max(await db.Messages.IgnoreQueryFilters().Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0, await db.InternalNotes.IgnoreQueryFilters().Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0) + 1;
        var message = new Message { TenantId = channel.TenantId, ChannelId = channel.Id, ConversationId = conversation.Id, Direction = MessageDirection.Inbound, Body = input.Text ?? $"[{input.MediaMimeType ?? "unsupported message"}]", ExternalMessageId = input.ExternalMessageId, Status = MessageStatus.Delivered, Sequence = sequence };
        conversation.RecordInboundActivity(message.CreatedAt); db.Messages.Add(message);
        Emit(channel.TenantId, "message.created", message.Id);
        Emit(channel.TenantId, "conversation.updated", conversation.Id);
    }

    private async Task ApplyStatusUpdateAsync(Channel channel, WhatsAppStatusUpdate update, CancellationToken token)
    {
        var message = await db.Messages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.ChannelId == channel.Id && x.ExternalMessageId == update.ExternalMessageId, token);
        if (message is null) return;
        var mapped = update.Status.ToLowerInvariant() switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" => MessageStatus.Read,
            "failed" => MessageStatus.Failed,
            _ => MessageStatus.Unknown,
        };
        if (message.Status == mapped) return;
        message.Status = mapped;
        if (mapped == MessageStatus.Failed) message.FailureReason = "provider_rejected";
        Emit(message.TenantId, "message.statusChanged", message.Id);
    }

    private async Task NotifyAdminsAsync(Guid tenantId, string type, string text, CancellationToken token)
    {
        db.Notifications.Add(new NotificationEntity { TenantId = tenantId, Type = type, Text = text });
        Emit(tenantId, "notification.created", Guid.NewGuid());
        await db.SaveChangesAsync(token);
    }

    private void Emit(Guid tenantId, string type, Guid id) =>
        db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), tenantId, type, JsonSerializer.Serialize(new { id }), DateTimeOffset.UtcNow));
}
