using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class PersistentInboxService(InboxDbContext db, ICurrentTenant current) : IInboxService
{
    public async Task<ConversationPage> ListAsync(string? search, ConversationStatus? status, string? channel, bool unreadOnly, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize, 1, 100);
        var query = SummaryQuery();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(x => x.Platform == channel);
        if (unreadOnly) query = query.Where(x => x.Unread);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(x => x.ContactName.ToLower().Contains(term) || x.Preview.ToLower().Contains(term) || x.Platform.ToLower().Contains(term) || x.Id.ToString().Contains(term)); }
        if (DecodeCursor(cursor) is { } before) query = query.Where(x => x.UpdatedAt < before);
        var items = await query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).Take(take + 1).ToListAsync(cancellationToken);
        var hasMore = items.Count > take; if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, hasMore && items.Count > 0 ? EncodeCursor(items[^1].UpdatedAt) : null);
    }

    public Task<ConversationDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Conversations.Where(x => x.Id == id).Join(db.Contacts, c => c.ContactId, p => p.Id, (c, p) => new { c, p }).Join(db.Channels, x => x.c.ChannelId, ch => ch.Id,
            (x, ch) => new ConversationDetails(x.c.Id, x.c.Status, x.c.UpdatedAt, x.c.LastReadSequence, ch.Id, ch.Platform, x.p.Id, x.p.DisplayName, x.p.Phone, x.p.Email, x.p.Notes)).SingleOrDefaultAsync(cancellationToken);

    public async Task<ActivityResponse?> ActivityAsync(Guid id, long? before, int pageSize, CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(x => x.Id == id, cancellationToken)) return null;
        var take = Math.Clamp(pageSize, 1, 100);
        var messages = db.Messages.Where(x => x.ConversationId == id && (before == null || x.Sequence < before)).Select(x => new ActivityItem(ActivityKind.Message, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.SenderUserId, x.Status));
        var notes = db.InternalNotes.Where(x => x.ConversationId == id && (before == null || x.Sequence < before)).Select(x => new ActivityItem(ActivityKind.InternalNote, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.AuthorId, null));
        var items = await messages.Concat(notes).OrderByDescending(x => x.Sequence).Take(take).ToListAsync(cancellationToken);
        return new(items, items.Count == take ? items[^1].Sequence.ToString() : null);
    }

    public async Task<ActivityItem?> AddNoteAsync(Guid id, string body, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (conversation is null || current.UserId is not { } userId) return null;
        var sequence = await NextSequence(id, cancellationToken); var note = new InternalNote { TenantId = conversation.TenantId, ConversationId = id, AuthorId = userId, Body = RequireBody(body), Sequence = sequence };
        db.InternalNotes.Add(note); Touch(conversation); AddOutbox(conversation.TenantId, "note.created", note.Id); await db.SaveChangesAsync(cancellationToken);
        return new(ActivityKind.InternalNote, note.Id, id, note.Body, note.CreatedAt, sequence, userId, null);
    }

    public async Task<ActivityItem?> SendAsync(Guid id, string body, string idempotencyKey, string? templateName, IReadOnlyList<Guid>? attachmentIds, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (conversation is null || current.UserId is not { } userId) return null;
        var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == id && x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return ToActivity(existing);
        // 24-hour customer-service window enforced before accepting free-form sends.
        var policy = new WhatsAppMessagingPolicy();
        var decision = policy.Evaluate(conversation.LastCustomerMessageAt, DateTimeOffset.UtcNow, hasApprovedTemplate: !string.IsNullOrWhiteSpace(templateName));
        if (decision == WhatsAppSendDecision.TemplateRequired)
            throw new InboxException("messaging_window_closed", "The 24-hour customer service window is closed. Send an approved template message.", 422);
        var sequence = await NextSequence(id, cancellationToken); var message = new Message { TenantId = conversation.TenantId, ChannelId = conversation.ChannelId, ConversationId = id, Direction = MessageDirection.Outbound, SenderUserId = userId, Body = RequireBody(body), IdempotencyKey = idempotencyKey, Status = MessageStatus.Pending, Sequence = sequence };
        db.Messages.Add(message);
        if (attachmentIds is { Count: > 0 })
        {
            var ready = await db.Attachments.Where(x => attachmentIds.Contains(x.Id)).ToListAsync(cancellationToken);
            if (ready.Count != attachmentIds.Count) throw new InboxException("attachment_not_found", "One or more attachments were not found.", 400);
            foreach (var attachment in ready)
            {
                if (attachment.TenantId != conversation.TenantId) throw new InboxException("attachment_not_found", "One or more attachments were not found.", 403);
                if (attachment.Status != AttachmentStatus.Staged || attachment.MessageId is not null) throw new InboxException("attachment_already_claimed", "An attachment was already claimed or is no longer staged.", 409);
                if (attachment.ExpiresAt <= DateTimeOffset.UtcNow) throw new InboxException("attachment_expired", "An attachment staging record has expired.", 410);
                attachment.MessageId = message.Id;
                attachment.Status = AttachmentStatus.Claimed;
            }
        }
        Touch(conversation); AddOutbox(conversation.TenantId, "outbound.message.requested", message.Id); await db.SaveChangesAsync(cancellationToken); return ToActivity(message);
    }

    public async Task<ConversationSummary?> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken cancellationToken) { var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (c is null) return null; c.Status = status; Touch(c); AddOutbox(c.TenantId, "conversation.updated", c.Id); await AuditAndSave(c.TenantId, "conversation.status.changed", c.Id, cancellationToken); return await SummaryQuery().SingleAsync(x => x.Id == id, cancellationToken); }
    public async Task<ConversationSummary?> MarkReadAsync(Guid id, long throughSequence, CancellationToken cancellationToken) { var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (c is null) return null; c.LastReadSequence = Math.Max(c.LastReadSequence, throughSequence); await db.SaveChangesAsync(cancellationToken); return await SummaryQuery().SingleAsync(x => x.Id == id, cancellationToken); }
    public async Task<bool> UpdateCustomerNotesAsync(Guid id, string? notes, CancellationToken cancellationToken) { var contact = await db.Conversations.Where(x => x.Id == id).Join(db.Contacts, x => x.ContactId, x => x.Id, (_, contact) => contact).SingleOrDefaultAsync(cancellationToken); if (contact is null) return false; contact.Notes = notes?.Trim(); await AuditAndSave(contact.TenantId, "contact.notes.updated", contact.Id, cancellationToken); return true; }

    private IQueryable<ConversationSummary> SummaryQuery() => db.Conversations.Select(c => new ConversationSummary(c.Id, db.Contacts.Where(p => p.Id == c.ContactId).Select(p => p.DisplayName).First(), db.Channels.Where(ch => ch.Id == c.ChannelId).Select(ch => ch.Platform).First(), db.Messages.Where(m => m.ConversationId == c.Id).OrderByDescending(m => m.Sequence).Select(m => m.Body).FirstOrDefault() ?? "", c.Status, db.Messages.Any(m => m.ConversationId == c.Id && m.Direction == MessageDirection.Inbound && m.Sequence > c.LastReadSequence), c.UpdatedAt));
    private async Task<long> NextSequence(Guid conversationId, CancellationToken token) { var messageMax = await db.Messages.Where(x => x.ConversationId == conversationId).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0; var noteMax = await db.InternalNotes.Where(x => x.ConversationId == conversationId).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0; return Math.Max(messageMax, noteMax) + 1; }
    private static string RequireBody(string body) => string.IsNullOrWhiteSpace(body) ? throw new ArgumentException("Body is required.") : body.Trim();
    private static void Touch(Conversation conversation) => conversation.UpdatedAt = DateTimeOffset.UtcNow;
    private void AddOutbox(Guid tenantId, string type, Guid id) => db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), tenantId, type, JsonSerializer.Serialize(new { id }), DateTimeOffset.UtcNow));
    private async Task AuditAndSave(Guid tenantId, string action, Guid resource, CancellationToken token) { db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = current.UserId, Action = action, Resource = resource.ToString() }); await db.SaveChangesAsync(token); }
    private static ActivityItem ToActivity(Message message) => new(ActivityKind.Message, message.Id, message.ConversationId, message.Body, message.CreatedAt, message.Sequence, message.SenderUserId, message.Status);
    private static string EncodeCursor(DateTimeOffset value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString("O")));
    private static DateTimeOffset? DecodeCursor(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; try { return DateTimeOffset.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))); } catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new ArgumentException("Invalid cursor.", nameof(value)); } }
}
