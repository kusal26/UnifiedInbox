using System.Collections.Concurrent;
using System.Security.Cryptography;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Infrastructure;

public sealed record StagedAttachment(Guid Id, Guid TenantId, Guid UploaderId, string FileName, string ContentType, long Size, bool Claimed, DateTimeOffset ExpiresAt);
public sealed record CannedResponse(Guid Id, Guid TenantId, string Title, string Shortcut, string Content);
public sealed record AuditEntry(Guid Id, Guid TenantId, Guid ActorId, string Action, string Resource, DateTimeOffset CreatedAt);
public sealed record Notification(Guid Id, Guid TenantId, string Type, string Text, bool Read, DateTimeOffset CreatedAt);
public sealed record WebhookReceipt(Guid Id, Guid ChannelId, string ProviderEventId, byte[] RawBody, DateTimeOffset ReceivedAt);

public sealed partial class InMemoryInboxStore
{
    private readonly List<StagedAttachment> attachments = [];
    private readonly List<CannedResponse> cannedResponses = [];
    private readonly List<AuditEntry> auditEntries = [];
    private readonly List<Notification> notifications = [];
    private readonly List<WebhookReceipt> webhookReceipts = [];
    public IReadOnlyCollection<StagedAttachment> Attachments => attachments;
    public IReadOnlyCollection<CannedResponse> CannedResponses => cannedResponses;
    public IReadOnlyCollection<AuditEntry> AuditEntries => auditEntries;
    public IReadOnlyCollection<Notification> Notifications => notifications;
    public IReadOnlyCollection<WebhookReceipt> WebhookReceipts => webhookReceipts;
    public bool PersistWebhook(Guid channelId, string providerEventId, byte[] rawBody) { lock (gate) { if (webhookReceipts.Any(x => x.ChannelId == channelId && x.ProviderEventId == providerEventId)) return false; webhookReceipts.Add(new(Guid.NewGuid(), channelId, providerEventId, rawBody, DateTimeOffset.UtcNow)); return true; } }

    public StagedAttachment StageAttachment(Guid tenantId, Guid uploaderId, string fileName, string contentType, long size)
    {
        if (size <= 0 || size > 25 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var allowed = new[] { "image/jpeg", "image/png", "application/pdf", "video/mp4" };
        if (!allowed.Contains(contentType, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsupported media type");
        var item = new StagedAttachment(Guid.NewGuid(), tenantId, uploaderId, fileName, contentType, size, false, DateTimeOffset.UtcNow.AddHours(1));
        lock (gate) { attachments.Add(item); auditEntries.Add(new(Guid.NewGuid(), tenantId, uploaderId, "attachment.staged", item.Id.ToString(), DateTimeOffset.UtcNow)); }
        return item;
    }

    public IReadOnlyList<StagedAttachment> ClaimAttachments(Guid tenantId, Guid uploaderId, IEnumerable<Guid> ids)
    {
        lock (gate)
        {
            var selected = attachments.Where(x => ids.Contains(x.Id)).ToList();
            if (selected.Any(x => x.TenantId != tenantId || x.UploaderId != uploaderId || x.Claimed || x.ExpiresAt <= DateTimeOffset.UtcNow)) throw new InvalidOperationException("Attachment cannot be claimed");
            for (var i = 0; i < attachments.Count; i++) if (selected.Any(x => x.Id == attachments[i].Id)) attachments[i] = attachments[i] with { Claimed = true };
            return selected.Select(x => x with { Claimed = true }).ToList();
        }
    }

    public CannedResponse AddCanned(Guid tenantId, Guid actorId, string title, string shortcut, string content) { EnsureAdmin(tenantId, actorId); var item = new CannedResponse(Guid.NewGuid(), tenantId, title, shortcut, content); cannedResponses.Add(item); Audit(tenantId, actorId, "canned_response.created", item.Id.ToString()); return item; }
    public void AddAudit(Guid tenantId, Guid actorId, string action, string resource) => Audit(tenantId, actorId, action, resource);
    public void AddUser(Guid tenantId, Guid actorId, string email, string displayName, UserRole role) { EnsureAdmin(tenantId, actorId); users.Add(new User(Guid.NewGuid(), tenantId, email, displayName, role)); Audit(tenantId, actorId, "user.created", email); }
    public void EnsureAdmin(Guid tenantId, Guid actorId) { if (!users.Any(x => x.Id == actorId && x.TenantId == tenantId && x.Role is UserRole.Owner or UserRole.Admin)) throw new UnauthorizedAccessException(); }
    public void CleanupExpiredAttachments() { lock (gate) attachments.RemoveAll(x => !x.Claimed && x.ExpiresAt <= DateTimeOffset.UtcNow); }
    public void AddNotification(Guid tenantId, string type, string text) => notifications.Add(new(Guid.NewGuid(), tenantId, type, text, false, DateTimeOffset.UtcNow));
    private void Audit(Guid tenantId, Guid actorId, string action, string resource) => auditEntries.Add(new(Guid.NewGuid(), tenantId, actorId, action, resource, DateTimeOffset.UtcNow));
}
