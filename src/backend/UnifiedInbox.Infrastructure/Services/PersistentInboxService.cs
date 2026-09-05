using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class PersistentInboxService(InboxDbContext db, ICurrentTenant current, IAttachmentService attachments, IWhatsAppTemplateService templates) : IInboxService
{
    public async Task<ConversationPage> ListAsync(string? search, ConversationStatus? status, string? channel, bool unreadOnly, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize, 1, 100);
        var conversations = Filtered(db.Conversations, search, status, channel, unreadOnly, DecodeCursor(cursor));
        // Order and paginate on the base entity before projecting: EF Core cannot translate
        // ordering over the projected summary when that projection contains correlated subqueries
        // under the tenant query filter (observed as a 500 on the live list endpoint).
        var ids = await conversations
            .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
            .Take(take + 1)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var hasMore = ids.Count > take;
        if (hasMore) ids.RemoveAt(ids.Count - 1);
        var items = await ProjectSummaries(db.Conversations.Where(x => ids.Contains(x.Id)).OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)).ToListAsync(cancellationToken);
        return new(items, hasMore && items.Count > 0 ? EncodeCursor(items[^1].UpdatedAt) : null);
    }

    private IQueryable<Conversation> Filtered(IQueryable<Conversation> query, string? search, ConversationStatus? status, string? channel, bool unreadOnly, DateTimeOffset? before)
    {
        if (status is not null) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(x => db.Channels.Where(ch => ch.Id == x.ChannelId).Select(ch => ch.Platform).First() == channel);
        if (unreadOnly) query = query.Where(x => db.Messages.Any(m => m.ConversationId == x.Id && m.Direction == MessageDirection.Inbound && m.Sequence > x.LastReadSequence));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => db.Contacts.Where(p => p.Id == x.ContactId).Select(p => p.DisplayName).First().ToLower().Contains(term)
                || (db.Messages.Where(m => m.ConversationId == x.Id).OrderByDescending(m => m.Sequence).Select(m => m.Body).FirstOrDefault() ?? "").ToLower().Contains(term)
                || db.Channels.Where(ch => ch.Id == x.ChannelId).Select(ch => ch.Platform).First().ToLower().Contains(term)
                || x.Id.ToString().Contains(term));
        }
        if (before is not null) query = query.Where(x => x.UpdatedAt < before);
        return query;
    }

    private IQueryable<ConversationSummary> ProjectSummaries(IQueryable<Conversation> query) =>
        query.Select(x => new ConversationSummary(x.Id, db.Contacts.Where(p => p.Id == x.ContactId).Select(p => p.DisplayName).First(), db.Channels.Where(ch => ch.Id == x.ChannelId).Select(ch => ch.Platform).First(), db.Messages.Where(m => m.ConversationId == x.Id).OrderByDescending(m => m.Sequence).Select(m => m.Body).FirstOrDefault() ?? "", x.Status, db.Messages.Any(m => m.ConversationId == x.Id && m.Direction == MessageDirection.Inbound && m.Sequence > x.LastReadSequence), x.UpdatedAt));

    public Task<ConversationDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Conversations.Where(x => x.Id == id).Join(db.Contacts, c => c.ContactId, p => p.Id, (c, p) => new { c, p }).Join(db.Channels, x => x.c.ChannelId, ch => ch.Id,
            (x, ch) => new ConversationDetails(x.c.Id, x.c.Status, x.c.UpdatedAt, x.c.LastReadSequence, ch.Id, ch.Platform, x.p.Id, x.p.DisplayName, x.p.Phone, x.p.Email, x.p.Notes)).SingleOrDefaultAsync(cancellationToken);

    public async Task<ActivityResponse?> ActivityAsync(Guid id, long? before, int pageSize, CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AnyAsync(x => x.Id == id, cancellationToken)) return null;
        var take = Math.Clamp(pageSize, 1, 100);
        // Query each tenant-scoped timeline table with the shared per-conversation sequence, then
        // merge in memory. A database-level UNION of projected messages + notes cannot be translated
        // by EF Core when each side is projected under the tenant query filter, so we keep ordering
        // entirely in the application after two bounded, ordered reads.
        var messages = await db.Messages
            .Where(x => x.ConversationId == id && (before == null || x.Sequence < before))
            .OrderByDescending(x => x.Sequence).Take(take)
            .Select(x => new ActivityItem(ActivityKind.Message, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.SenderUserId, x.Status))
            .ToListAsync(cancellationToken);
        var notes = await db.InternalNotes
            .Where(x => x.ConversationId == id && (before == null || x.Sequence < before))
            .OrderByDescending(x => x.Sequence).Take(take)
            .Select(x => new ActivityItem(ActivityKind.InternalNote, x.Id, id, x.Body, x.CreatedAt, x.Sequence, x.AuthorId, null))
            .ToListAsync(cancellationToken);
        var items = messages.Concat(notes).OrderByDescending(x => x.Sequence).Take(take).ToList();
        return new(items, items.Count == take ? items[^1].Sequence.ToString() : null);
    }

    public async Task<ActivityItem?> AddNoteAsync(Guid id, string body, CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, Domain.UserRole.Agent, cancellationToken);
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (conversation is null || current.UserId is not { } userId) return null;
        var sequence = await NextSequence(id, cancellationToken); var note = new InternalNote { TenantId = conversation.TenantId, ConversationId = id, AuthorId = userId, Body = RequireBody(body), Sequence = sequence };
        db.InternalNotes.Add(note); Touch(conversation); AddOutbox(conversation.TenantId, "note.created", note.Id); AddOutbox(conversation.TenantId, "conversation.updated", conversation.Id); await db.SaveChangesAsync(cancellationToken);
        return new(ActivityKind.InternalNote, note.Id, id, note.Body, note.CreatedAt, sequence, userId, null);
    }

    public async Task<ActivityItem?> SendAsync(Guid id, OutboundMessageCommand command, CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, Domain.UserRole.Agent, token);
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, token); if (conversation is null || current.UserId is not { } userId) return null;
        var existing = await db.Messages.SingleOrDefaultAsync(x => x.ConversationId == id && x.IdempotencyKey == command.IdempotencyKey, token);
        if (existing is not null) return ToActivity(existing);
        var hasAttachments = command.AttachmentIds is { Count: > 0 };
        if (command.Template is not null && hasAttachments)
            throw new InboxException("template_invalid", "An approved template cannot be combined with attachments.", 422);
        // 24-hour customer-service window enforced before accepting free-form sends. A template
        // request is a structured identity, never proof of approval on a name alone.
        var policy = new WhatsAppMessagingPolicy();
        var decision = policy.Evaluate(conversation.LastCustomerMessageAt, DateTimeOffset.UtcNow, hasApprovedTemplate: command.Template is not null);
        if (decision == WhatsAppSendDecision.TemplateRequired)
            throw new InboxException("messaging_window_closed", "The 24-hour customer service window is closed. Send an approved template message.", 422);
        if (command.Template is not null)
            await templates.ValidateAsync(conversation.ChannelId, command.Template, token); // template_invalid on unapproved/mismatched
        var sequence = await NextSequence(id, token);
        var message = new Message { TenantId = conversation.TenantId, ChannelId = conversation.ChannelId, ConversationId = id, Direction = MessageDirection.Outbound, SenderUserId = userId, Body = BodyFor(command, hasAttachments), IdempotencyKey = command.IdempotencyKey, Status = MessageStatus.Pending, Sequence = sequence, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        db.Messages.Add(message);
        Touch(conversation); AddOutbox(conversation.TenantId, "outbound.message.requested", message.Id); AddOutbox(conversation.TenantId, "message.created", message.Id); AddOutbox(conversation.TenantId, "conversation.updated", conversation.Id);

        // The message must be durable before attachments (or their delivery parts) can reference
        // it, and the claims must be atomic with it: a losing concurrent send must not leave a
        // message that references attachments it does not own. Run inside a transaction (the
        // ambient request-scope transaction when one is already open).
        var ownsTransaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null;
        if (ownsTransaction) await db.Database.BeginTransactionAsync(token);
        try
        {
            await db.SaveChangesAsync(token);
            var contentTypes = new Dictionary<Guid, string>();
            if (hasAttachments)
            {
                await attachments.ClaimForMessageAsync(message.Id, command.AttachmentIds!, token);
                contentTypes = await db.Attachments.Where(x => command.AttachmentIds!.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DetectedContentType ?? x.ContentType, token);
            }
            var specs = OutboundMessagePlanner.Plan(command, contentTypes);
            db.MessageDeliveryParts.AddRange(specs.Select((spec, index) => new MessageDeliveryPart
            {
                TenantId = conversation.TenantId,
                MessageId = message.Id,
                Position = index,
                Kind = spec.Kind,
                AttachmentId = spec.AttachmentId,
                TemplateName = spec.TemplateName,
                TemplateLanguage = spec.TemplateLanguage,
                TemplateComponentsJson = spec.TemplateComponentsJson,
                Status = MessageStatus.Pending,
            }));
            await db.SaveChangesAsync(token);
            if (ownsTransaction) await db.Database.CommitTransactionAsync(token);
        }
        catch
        {
            if (ownsTransaction) await db.Database.RollbackTransactionAsync(token);
            throw;
        }
        return ToActivity(message);
    }

    public async Task<ConversationSummary?> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, Domain.UserRole.Agent, cancellationToken);
        var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (c is null) return null;
        c.Status = status; Touch(c); AddOutbox(c.TenantId, "conversation.updated", c.Id);
        await AuditAndSave(c.TenantId, "conversation.status.changed", c.Id, cancellationToken);
        return await SummaryByIdAsync(id, cancellationToken);
    }
    public async Task<ConversationSummary?> MarkReadAsync(Guid id, long throughSequence, CancellationToken cancellationToken)
    {
        var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (c is null) return null;
        c.LastReadSequence = Math.Max(c.LastReadSequence, throughSequence);
        await db.SaveChangesAsync(cancellationToken);
        return await SummaryByIdAsync(id, cancellationToken);
    }
    public async Task<bool> UpdateCustomerNotesAsync(Guid id, string? notes, CancellationToken cancellationToken) { var contact = await db.Conversations.Where(x => x.Id == id).Join(db.Contacts, x => x.ContactId, x => x.Id, (_, contact) => contact).SingleOrDefaultAsync(cancellationToken); if (contact is null) return false; contact.Notes = notes?.Trim(); await AuditAndSave(contact.TenantId, "contact.notes.updated", contact.Id, cancellationToken); return true; }

    private async Task<ConversationSummary?> SummaryByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // Order/filter on the base entity before projecting (see ListAsync). Returning the summary
        // from a projection created eagerly against the query filter is not translatable by EF Core.
        return await db.Conversations
            .Where(x => x.Id == id)
            .Select(x => new ConversationSummary(x.Id, db.Contacts.Where(p => p.Id == x.ContactId).Select(p => p.DisplayName).First(), db.Channels.Where(ch => ch.Id == x.ChannelId).Select(ch => ch.Platform).First(), db.Messages.Where(m => m.ConversationId == x.Id).OrderByDescending(m => m.Sequence).Select(m => m.Body).FirstOrDefault() ?? "", x.Status, db.Messages.Any(m => m.ConversationId == x.Id && m.Direction == MessageDirection.Inbound && m.Sequence > x.LastReadSequence), x.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }
    private async Task<long> NextSequence(Guid conversationId, CancellationToken token) { var messageMax = await db.Messages.Where(x => x.ConversationId == conversationId).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0; var noteMax = await db.InternalNotes.Where(x => x.ConversationId == conversationId).Select(x => (long?)x.Sequence).MaxAsync(token) ?? 0; return Math.Max(messageMax, noteMax) + 1; }
    private static string RequireBody(string body) => string.IsNullOrWhiteSpace(body) ? throw new ArgumentException("Body is required.") : body.Trim();
    private static string BodyFor(OutboundMessageCommand command, bool hasAttachments)
    {
        var body = command.Body?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(body) && command.Template is null && !hasAttachments) throw new ArgumentException("Body is required.");
        return body;
    }
    private static void Touch(Conversation conversation) => conversation.UpdatedAt = DateTimeOffset.UtcNow;
    private void AddOutbox(Guid tenantId, string type, Guid id) => db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), tenantId, type, JsonSerializer.Serialize(new { id }), DateTimeOffset.UtcNow));
    private async Task AuditAndSave(Guid tenantId, string action, Guid resource, CancellationToken token) { db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = current.UserId, Action = action, Resource = resource.ToString() }); await db.SaveChangesAsync(token); }
    private static ActivityItem ToActivity(Message message) => new(ActivityKind.Message, message.Id, message.ConversationId, message.Body, message.CreatedAt, message.Sequence, message.SenderUserId, message.Status);
    private static string EncodeCursor(DateTimeOffset value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToString("O")));
    private static DateTimeOffset? DecodeCursor(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; try { return DateTimeOffset.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))); } catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new ArgumentException("Invalid cursor.", nameof(value)); } }
}
