using System.Collections.Concurrent;
using System.Security.Cryptography;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Infrastructure;

public sealed partial class InMemoryInboxStore : IInboxStore
{
    private readonly object gate = new();
    private long sequence;
    private readonly List<Tenant> tenants = [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "acme", "Acme Workspace")];
    private readonly List<User> users = [new(Guid.Parse("22222222-2222-2222-2222-222222222222"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "agent@acme.test", "Alex Agent", UserRole.Agent), new(Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "owner@acme.test", "Olivia Owner", UserRole.Owner)];
    private readonly List<Channel> channels = [new(Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "whatsapp", "business-acme")];
    private readonly List<Contact> contacts = [new(Guid.Parse("55555555-5555-5555-5555-555555555555"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "whatsapp", "business-acme", "customer-1", "Jamie Customer", "+15550000001")];
    private readonly List<Conversation> conversations = [];
    private readonly List<Message> messages = [];
    private readonly List<InternalNote> notes = [];
    private readonly List<OutboxEvent> outbox = [];
    private readonly ConcurrentDictionary<string, (Guid TenantId, Guid UserId)> sessions = new();
    private readonly ConcurrentDictionary<(Guid ConversationId, string Key), Message> idempotentMessages = new();
    public IReadOnlyCollection<Tenant> Tenants => tenants; public IReadOnlyCollection<User> Users => users; public IReadOnlyCollection<Channel> Channels => channels; public IReadOnlyCollection<Contact> Contacts => contacts; public IReadOnlyCollection<Conversation> Conversations => conversations; public IReadOnlyCollection<Message> Messages => messages; public IReadOnlyCollection<InternalNote> Notes => notes; public IReadOnlyCollection<OutboxEvent> Outbox => outbox;
    public string? Login(string tenantSlug, string email, string password) { if (string.IsNullOrWhiteSpace(password)) return null; var t = tenants.FirstOrDefault(x => x.Slug.Equals(tenantSlug, StringComparison.OrdinalIgnoreCase)); var u = t is null ? null : users.FirstOrDefault(x => x.TenantId == t.Id && x.Email.Equals(email, StringComparison.OrdinalIgnoreCase)); if (t is null || u is null) return null; EnsureConversation(t.Id); var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); sessions[token] = (t.Id, u.Id); return token; }
    public bool TrySession(string token, out Guid tenantId, out Guid userId) { if (sessions.TryGetValue(token, out var value)) { tenantId = value.TenantId; userId = value.UserId; return true; } tenantId = default; userId = default; return false; }
    public void AddInbound(Guid tenantId, Guid conversationId, string body, string? externalMessageId) { lock (gate) { if (externalMessageId is not null && messages.Any(x => x.TenantId == tenantId && x.ExternalMessageId == externalMessageId)) return; var m = new Message { TenantId = tenantId, ConversationId = conversationId, Direction = MessageDirection.Inbound, Body = body, ExternalMessageId = externalMessageId, Sequence = ++sequence }; messages.Add(m); Touch(conversationId); AddEvent(tenantId, "message.received", m.Id.ToString()); } }
    public Message AddOutbound(Guid tenantId, Guid conversationId, Guid senderId, string body, string idempotencyKey) { lock (gate) { if (idempotentMessages.TryGetValue((conversationId, idempotencyKey), out var existing)) return existing; var m = new Message { TenantId = tenantId, ConversationId = conversationId, Direction = MessageDirection.Outbound, Body = body, SenderUserId = senderId, Sequence = ++sequence, Status = MessageStatus.Pending }; messages.Add(m); idempotentMessages[(conversationId, idempotencyKey)] = m; Touch(conversationId); AddEvent(tenantId, "message.send_requested", m.Id.ToString()); return m; } }
    public InternalNote AddNote(Guid tenantId, Guid conversationId, Guid authorId, string body) { lock (gate) { var n = new InternalNote { TenantId = tenantId, ConversationId = conversationId, AuthorId = authorId, Body = body, Sequence = ++sequence }; notes.Add(n); Touch(conversationId); AddEvent(tenantId, "internal_note.created", n.Id.ToString()); return n; } }
    public Conversation SetStatus(Guid tenantId, Guid conversationId, ConversationStatus status) { lock (gate) { var c = GetConversation(tenantId, conversationId); c.Status = status; Touch(conversationId); AddEvent(tenantId, "conversation.status_changed", conversationId.ToString()); return c; } }
    public Conversation MarkRead(Guid tenantId, Guid conversationId, long throughSequence) { lock (gate) { var c = GetConversation(tenantId, conversationId); c.LastReadSequence = Math.Max(c.LastReadSequence, throughSequence); return c; } }
    public Conversation GetConversation(Guid tenantId, Guid id) => conversations.First(x => x.TenantId == tenantId && x.Id == id);
    public Conversation EnsureConversation(Guid tenantId) { lock (gate) { var c = conversations.FirstOrDefault(x => x.TenantId == tenantId); if (c is not null) return c; c = new Conversation { TenantId = tenantId, ChannelId = channels.First(x => x.TenantId == tenantId).Id, ContactId = contacts.First(x => x.TenantId == tenantId).Id }; conversations.Add(c); AddInbound(tenantId, c.Id, "Welcome to the shared inbox", "seed-1"); return c; } }
    private void Touch(Guid id) { var c = conversations.First(x => x.Id == id); c.UpdatedAt = DateTimeOffset.UtcNow; }
    private void AddEvent(Guid tenantId, string type, string payload) => outbox.Add(new OutboxEvent(Guid.NewGuid(), tenantId, type, payload, DateTimeOffset.UtcNow));
}
