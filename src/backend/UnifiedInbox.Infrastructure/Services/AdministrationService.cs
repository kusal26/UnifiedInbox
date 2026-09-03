using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AdministrationService(InboxDbContext db, ICurrentTenant current) : IAdministrationService
{
    public async Task<IReadOnlyList<User>> UsersAsync(CancellationToken token) => await db.Users.OrderBy(x => x.DisplayName).ToListAsync(token);
    public async Task<IReadOnlyList<Channel>> ChannelsAsync(CancellationToken token) => await db.Channels.OrderBy(x => x.DisplayName).ToListAsync(token);
    public async Task<IReadOnlyList<CannedResponseEntity>> CannedResponsesAsync(string? search, CancellationToken token) { var query = db.CannedResponses.AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) { var q = search.ToLower(); query = query.Where(x => x.Title.ToLower().Contains(q) || x.Shortcut.ToLower().Contains(q) || x.Content.ToLower().Contains(q)); } return await query.OrderBy(x => x.Title).ToListAsync(token); }
    public async Task<CannedResponseEntity> AddCannedResponseAsync(string title, string shortcut, string content, CancellationToken token) { RequireAdmin(); var item = new CannedResponseEntity { TenantId = current.TenantId!.Value, Title = title.Trim(), Shortcut = shortcut.Trim(), Content = content.Trim() }; db.CannedResponses.Add(item); Audit("canned-response.created", item.Id); await db.SaveChangesAsync(token); return item; }
    public async Task<IReadOnlyList<NotificationEntity>> NotificationsAsync(CancellationToken token) => await db.Notifications.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
    public async Task<IReadOnlyList<AuditEntryEntity>> AuditAsync(string? search, CancellationToken token) { RequireOwner(); var query = db.AuditEntries.AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) { var q = search.ToLower(); query = query.Where(x => x.Action.ToLower().Contains(q) || x.Resource.ToLower().Contains(q)); } return await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(token); }
    public Task<Tenant?> WorkspaceAsync(CancellationToken token) => current.TenantId is { } id ? db.Tenants.SingleOrDefaultAsync(x => x.Id == id, token) : Task.FromResult<Tenant?>(null);
    public async Task<Tenant?> UpdateWorkspaceAsync(string name, int retentionDays, CancellationToken token) { RequireAdmin(); var tenant = await WorkspaceAsync(token); if (tenant is null) return null; tenant.Name = name.Trim(); tenant.RetentionDays = Math.Clamp(retentionDays, 30, 3650); Audit("workspace.updated", tenant.Id); await db.SaveChangesAsync(token); return tenant; }
    private void Audit(string action, Guid resource) => db.AuditEntries.Add(new AuditEntryEntity { TenantId = current.TenantId!.Value, ActorId = current.UserId, Action = action, Resource = resource.ToString() });
    private void RequireAdmin() { if (current.Role is not UserRole.Owner and not UserRole.Admin) throw new UnauthorizedAccessException(); }
    private void RequireOwner() { if (current.Role is not UserRole.Owner) throw new UnauthorizedAccessException(); }
}

public sealed class WebhookService(InboxDbContext db) : IWebhookService
{
    public async Task<bool> PersistAsync(Guid channelId, string providerEventId, byte[] rawBody, CancellationToken token)
    {
        var channel = await db.Channels.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == channelId && x.IsEnabled, token); if (channel is null) return false;
        if (await db.WebhookReceipts.IgnoreQueryFilters().AnyAsync(x => x.ChannelId == channelId && x.ProviderEventId == providerEventId, token)) return true;
        var receipt = new global::UnifiedInbox.Domain.WebhookReceipt { TenantId = channel.TenantId, ChannelId = channelId, ProviderEventId = providerEventId, RawBody = rawBody };
        db.WebhookReceipts.Add(receipt); db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), channel.TenantId, "webhook.received", System.Text.Json.JsonSerializer.Serialize(new { receiptId = receipt.Id }), DateTimeOffset.UtcNow));
        channel.LastWebhookAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(token); return true;
    }
}
