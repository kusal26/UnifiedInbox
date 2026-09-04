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
/// New sends are modeled as durable delivery parts (text/template/attachment), each with its
/// own provider id, retry state, and status, aggregated onto one parent message row.
/// </summary>
public sealed class MessageProcessor(InboxDbContext db, WhatsAppMessageSender sender, ILogger<MessageProcessor> logger, InboundMediaIngestor? media = null)
{
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

    public async Task<WebhookOutcome> NormalizeWebhookAsync(Guid receiptId, CancellationToken token)
    {
        var receipt = await db.WebhookReceipts.SingleOrDefaultAsync(x => x.Id == receiptId, token);
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
            var channel = await db.Channels.SingleAsync(x => x.Id == receipt.ChannelId, token);
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
        var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == messageId, token);
        if (message is null) return OutboundOutcome.Ignored;
        var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == message.Id).OrderBy(x => x.Position).ToListAsync(token);
        // Legacy single-send rows predate delivery parts and keep their exact state machine.
        if (parts.Count == 0) return await SendLegacyOutboundAsync(message, token);
        return await SendPartedOutboundAsync(message, parts, token);
    }

    /// <summary>Original provider send for messages that predate delivery parts (no parts rows).</summary>
    private async Task<OutboundOutcome> SendLegacyOutboundAsync(Message message, CancellationToken token)
    {
        if (message.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed) return OutboundOutcome.Ignored;
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

        var conversation = await db.Conversations.SingleAsync(x => x.Id == message.ConversationId, token);
        var channel = await db.Channels.SingleAsync(x => x.Id == message.ChannelId, token);
        var contact = await db.Contacts.SingleAsync(x => x.Id == conversation.ContactId, token);
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
            logger.LogWarning(exception, "Outbound message {MessageId} has an ambiguous outcome; reconciling instead of resending", message.Id);
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
            logger.LogWarning(exception, "Outbound message {MessageId} failed transiently (attempt {Attempt})", message.Id, message.Attempts);
            message.Status = MessageStatus.Pending;
            message.FailureReason = exception.GetType().Name;
            message.NextAttemptAt = DateTimeOffset.UtcNow.Add(OutboxRetryPolicy.NextDelay(message.Attempts));
            await db.SaveChangesAsync(token);
            return OutboundOutcome.RetryScheduled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Outbound message {MessageId} dead-lettered", message.Id);
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

    /// <summary>
    /// Drives the durable delivery parts of one timeline item. Every part is claimed with a
    /// row-version guard, its provider request id is persisted before the HTTP call, and its
    /// own provider message id and status are stored independently. The parent message status is
    /// only ever an aggregate: Sent once every part succeeds, Failed on a permanent part failure.
    /// </summary>
    private async Task<OutboundOutcome> SendPartedOutboundAsync(Message message, IReadOnlyList<MessageDeliveryPart> parts, CancellationToken token)
    {
        if (message.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed) return OutboundOutcome.Ignored;
        var conversation = await db.Conversations.SingleAsync(x => x.Id == message.ConversationId, token);
        var channel = await db.Channels.SingleAsync(x => x.Id == message.ChannelId, token);
        var contact = await db.Contacts.SingleAsync(x => x.Id == conversation.ContactId, token);
        if (!channel.IsEnabled)
        {
            await FailMessageAsync(message, parts, "channel_disabled", token);
            return OutboundOutcome.Failed;
        }
        var hasTemplate = parts.Any(x => x.Kind == DeliveryPartKind.Template);
        // Defense in depth: a free-form message whose window lapsed before the worker ran can no
        // longer be sent, while an approved template part is allowed outside the window.
        if (new WhatsAppMessagingPolicy().Evaluate(conversation.LastCustomerMessageAt, DateTimeOffset.UtcNow, hasApprovedTemplate: hasTemplate) == WhatsAppSendDecision.TemplateRequired)
        {
            await FailMessageAsync(message, parts, "template_required", token);
            return OutboundOutcome.Failed;
        }

        var now = DateTimeOffset.UtcNow;
        var outcome = OutboundOutcome.Reconciled;
        var notified = false;
        var sentAny = false;
        foreach (var part in parts)
        {
            switch (part.Status)
            {
                case MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed:
                    continue;
                case MessageStatus.Unknown:
                    // Reconcile an ambiguous part only when the provider actually returned an id.
                    if (part.ExternalMessageId is not null)
                    {
                        part.Status = MessageStatus.Sent;
                        part.NextAttemptAt = null;
                        outcome = OutboundOutcome.Reconciled;
                        await db.SaveChangesAsync(token);
                    }
                    continue;
                case MessageStatus.Sending:
                    // Still inside another worker's visibility timeout: leave it alone.
                    if (part.NextAttemptAt is not null && part.NextAttemptAt > now) continue;
                    // A previous attempt never reported back (crash or lost response): reconcile, never blindly resend.
                    part.Status = MessageStatus.Unknown;
                    part.NextAttemptAt = null;
                    await db.SaveChangesAsync(token);
                    if (!notified)
                    {
                        await NotifyAdminsAsync(message.TenantId, "message.failed", "A message part send was interrupted and needs review before retrying.", token);
                        notified = true;
                    }
                    outcome = OutboundOutcome.Reconciled;
                    continue;
                case MessageStatus.Pending:
                    if (part.NextAttemptAt is not null && part.NextAttemptAt > now) continue; // scheduled retry not yet due
                    if (!await TryClaimPartAsync(part, now, token)) continue; // another worker won the claim
                    try
                    {
                        var providerId = await sender.SendPartAsync(db, channel, contact, message.Body, part, token);
                        part.Status = MessageStatus.Sent;
                        part.ExternalMessageId = providerId;
                        part.NextAttemptAt = null;
                        channel.LastOutboundAt = now;
                        sentAny = true;
                        outcome = OutboundOutcome.Sent;
                        await db.SaveChangesAsync(token);
                    }
                    catch (Exception exception) when (OutboxRetryPolicy.IsAmbiguous(exception))
                    {
                        logger.LogWarning(exception, "Delivery part {PartId} of message {MessageId} has an ambiguous outcome; reconciling instead of resending", part.Id, message.Id);
                        part.Status = MessageStatus.Unknown;
                        part.NextAttemptAt = null;
                        await db.SaveChangesAsync(token);
                        if (!notified)
                        {
                            await NotifyAdminsAsync(message.TenantId, "message.failed", "A message part may or may not have been delivered and needs review.", token);
                            notified = true;
                        }
                        outcome = OutboundOutcome.Failed;
                    }
                    catch (Exception exception) when (OutboxRetryPolicy.IsTransient(exception) && part.Attempts < OutboxRetryPolicy.MaxAttempts)
                    {
                        logger.LogWarning(exception, "Delivery part {PartId} of message {MessageId} failed transiently (attempt {Attempt})", part.Id, message.Id, part.Attempts);
                        part.Status = MessageStatus.Pending;
                        part.NextAttemptAt = DateTimeOffset.UtcNow.Add(OutboxRetryPolicy.NextDelay(part.Attempts));
                        await db.SaveChangesAsync(token);
                        outcome = OutboundOutcome.RetryScheduled;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Delivery part {PartId} of message {MessageId} dead-lettered", part.Id, message.Id);
                        part.Status = MessageStatus.Failed;
                        part.NextAttemptAt = null;
                        db.ChannelHealth.Add(new ChannelHealth { TenantId = message.TenantId, ChannelId = message.ChannelId, IsHealthy = false, Reason = "repeated_send_failures" });
                        if (!notified)
                        {
                            await NotifyAdminsAsync(message.TenantId, "message.failed", $"A message part could not be delivered ({exception.GetType().Name}).", token);
                            notified = true;
                        }
                        // A permanent part failure fails the timeline item; remaining parts are settled
                        // so the sweeper never re-drives a message whose parent is already Failed.
                        await FailMessageAsync(message, parts, "provider_rejected", token);
                        return OutboundOutcome.Failed;
                    }
                    continue;
            }
        }
        if (sentAny) Emit(message.TenantId, "channel.updated", channel.Id);
        await SyncParentAsync(message, token);
        return outcome;
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

    private async Task<bool> TryClaimPartAsync(MessageDeliveryPart part, DateTimeOffset now, CancellationToken token)
    {
        part.Status = MessageStatus.Sending;
        part.Attempts++;
        part.NextAttemptAt = now.Add(VisibilityTimeout);
        part.ProviderRequestId ??= Guid.NewGuid().ToString("N");
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { db.Entry(part).Reload(); return false; } // another worker claimed it
    }

    /// <summary>Persists the parent aggregate: Sent only when every part is sent, Failed on a
    /// permanent part failure. The parent's retry time mirrors the soonest outstanding part.</summary>
    private async Task SyncParentAsync(Message message, CancellationToken token)
    {
        var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == message.Id).OrderBy(x => x.Position).ToListAsync(token);
        ApplyAggregate(message, parts);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { db.Entry(message).Reload(); } // a concurrent worker synced the parent
    }

    private async Task FailMessageAsync(Message message, IReadOnlyList<MessageDeliveryPart> parts, string reason, CancellationToken token)
    {
        message.Status = MessageStatus.Failed;
        message.FailureReason = reason;
        message.NextAttemptAt = null;
        foreach (var part in parts.Where(x => x.Status is MessageStatus.Pending or MessageStatus.Sending))
        {
            part.Status = MessageStatus.Failed;
            part.NextAttemptAt = null;
        }
        Emit(message.TenantId, "message.statusChanged", message.Id);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { db.Entry(message).Reload(); }
    }

    /// <summary>
    /// Conservative aggregation: a parent never reports more progress than its least-advanced
    /// part. Any pending/sending part keeps the item in flight; a failed part fails the item.
    /// </summary>
    private void ApplyAggregate(Message message, IReadOnlyList<MessageDeliveryPart> parts)
    {
        var previous = message.Status;
        var states = parts.Select(x => x.Status).ToList();
        MessageStatus next;
        if (states.Any(x => x == MessageStatus.Failed)) next = MessageStatus.Failed;
        else if (states.Any(x => x is MessageStatus.Pending or MessageStatus.Sending)) next = MessageStatus.Sending;
        else if (states.Any(x => x == MessageStatus.Unknown)) next = MessageStatus.Unknown;
        else if (states.All(x => x == MessageStatus.Read)) next = MessageStatus.Read;
        else if (states.All(x => x is MessageStatus.Delivered or MessageStatus.Read)) next = MessageStatus.Delivered;
        else next = MessageStatus.Sent;

        message.Status = next;
        message.FailureReason = next switch
        {
            MessageStatus.Failed => message.FailureReason ?? "provider_rejected",
            MessageStatus.Unknown => message.FailureReason ?? "ambiguous_provider_outcome",
            MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read => null,
            _ => null,
        };
        message.NextAttemptAt = next is MessageStatus.Pending or MessageStatus.Sending
            ? parts.Where(p => (p.Status is MessageStatus.Pending or MessageStatus.Sending) && p.NextAttemptAt is not null).Select(p => p.NextAttemptAt).Min()
            : null;
        if (next != previous) Emit(message.TenantId, "message.statusChanged", message.Id);
    }

    private async Task PersistInboundAsync(Channel channel, WhatsAppInbound input, CancellationToken token)
    {
        if (await db.Messages.AnyAsync(x => x.ChannelId == channel.Id && x.ExternalMessageId == input.ExternalMessageId, token)) return;
        var foundContact = await db.Contacts.SingleOrDefaultAsync(x => x.Platform == channel.Platform && x.ExternalAccountId == channel.ExternalAccountId && x.ExternalCustomerId == input.CustomerId, token);
        var contact = foundContact ?? new Contact(Guid.NewGuid(), channel.TenantId, channel.Platform, channel.ExternalAccountId, input.CustomerId, input.CustomerId, input.CustomerId);
        var newContact = foundContact is null;
        var foundConversation = await db.Conversations.SingleOrDefaultAsync(x => x.ChannelId == channel.Id && x.ExternalConversationId == input.CustomerId, token);
        var conversation = foundConversation ?? new Conversation { TenantId = channel.TenantId, ChannelId = channel.Id, ContactId = contact.Id, ExternalConversationId = input.CustomerId };
        var newConversation = foundConversation is null;
        var sequence = Math.Max(await db.Messages.Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0, await db.InternalNotes.Where(x => x.ConversationId == conversation.Id).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0) + 1;
        var message = new Message { TenantId = channel.TenantId, ChannelId = channel.Id, ConversationId = conversation.Id, Direction = MessageDirection.Inbound, Body = InboundBody(input), ExternalMessageId = input.ExternalMessageId, Status = MessageStatus.Delivered, Sequence = sequence };
        // Supported media is ingested privately (download, scan, tenant-scoped store) BEFORE any
        // row is added, so a transient ingest failure rolls back the whole webhook and no message
        // (or empty conversation) is left behind. The message, its attachment, and a new
        // conversation commit together in the normalization transaction.
        if (media is not null && input.MediaId is not null && input.Kind is WhatsAppInboundKind.Image or WhatsAppInboundKind.Video or WhatsAppInboundKind.Document)
            await media.IngestAsync(channel, message, input, token);
        if (newContact) db.Contacts.Add(contact);
        if (newConversation)
        {
            db.Conversations.Add(conversation);
            Emit(channel.TenantId, "conversation.created", conversation.Id);
        }
        conversation.RecordInboundActivity(message.CreatedAt); db.Messages.Add(message);
        Emit(channel.TenantId, "message.created", message.Id);
        Emit(channel.TenantId, "conversation.updated", conversation.Id);
    }

    private static string InboundBody(WhatsAppInbound input)
    {
        if (!string.IsNullOrWhiteSpace(input.Text)) return input.Text;
        return input.Kind switch
        {
            WhatsAppInboundKind.Image => "[image]",
            WhatsAppInboundKind.Video => "[video]",
            WhatsAppInboundKind.Audio => "[audio message]",
            WhatsAppInboundKind.Document => "[document]",
            WhatsAppInboundKind.Sticker => "[sticker]",
            _ => string.IsNullOrWhiteSpace(input.DeclaredMimeType) ? "[unsupported message]" : $"[{input.DeclaredMimeType}]",
        };
    }

    private async Task ApplyStatusUpdateAsync(Channel channel, WhatsAppStatusUpdate update, CancellationToken token)
    {
        var mapped = update.Status.ToLowerInvariant() switch
        {
            "sent" => MessageStatus.Sent,
            "delivered" => MessageStatus.Delivered,
            "read" => MessageStatus.Read,
            "failed" => MessageStatus.Failed,
            _ => MessageStatus.Unknown,
        };
        // New sends resolve status callbacks by delivery-part provider id; the parent message row
        // holds its own provider id only for legacy single-send rows.
        var part = await db.MessageDeliveryParts
            .Where(p => p.ExternalMessageId == update.ExternalMessageId)
            .Join(db.Messages, p => p.MessageId, m => m.Id, (p, m) => new { p, m })
            .Where(x => x.m.ChannelId == channel.Id)
            .Select(x => x.p).SingleOrDefaultAsync(token);
        if (part is not null)
        {
            if (part.Status == mapped) return;
            part.Status = mapped;
            var message = await db.Messages.SingleAsync(x => x.Id == part.MessageId, token);
            var parts = await db.MessageDeliveryParts.Where(x => x.MessageId == message.Id).OrderBy(x => x.Position).ToListAsync(token);
            ApplyAggregate(message, parts);
            return;
        }
        var legacy = await db.Messages.SingleOrDefaultAsync(x => x.ChannelId == channel.Id && x.ExternalMessageId == update.ExternalMessageId, token);
        if (legacy is null) return;
        if (legacy.Status == mapped) return;
        legacy.Status = mapped;
        if (mapped == MessageStatus.Failed) legacy.FailureReason = "provider_rejected";
        Emit(legacy.TenantId, "message.statusChanged", legacy.Id);
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
