using System.Text;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AdministrationService(InboxDbContext db, ICurrentTenant current) : IAdministrationService
{
    private static readonly string[] PreferenceKinds = ["message.received", "message.failed", "channel.unhealthy", "invitation.created"];

    public async Task<IReadOnlyList<User>> UsersAsync(CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        return await db.Users.OrderBy(x => x.DisplayName).ToListAsync(token);
    }

    public async Task<User> SetUserRoleAsync(Guid userId, UserRole role, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Owner, token);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, token) ?? throw new InboxException("user_not_found", "The user was not found.", 404);
        if (user.Id == actor.Id) throw new InboxException("cannot_change_own_role", "You cannot change your own role.", 400);
        user.Role = role;
        Audit(actor, "user.role.changed", user.Id, $"{{\"role\":\"{role}\"}}");
        await db.SaveChangesAsync(token);
        return user;
    }

    public async Task<User> SetUserActiveAsync(Guid userId, bool isActive, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == actor.TenantId, token) ?? throw new InboxException("user_not_found", "The user was not found.", 404);
        if (user.Id == actor.Id) throw new InboxException("cannot_deactivate_self", "You cannot deactivate your own account.", 400);
        if (actor.Role == UserRole.Admin && user.Role != UserRole.Agent)
            throw new InboxException("user_lifecycle_forbidden", "Administrators can only change agent accounts.", 403);
        user.IsActive = isActive;
        if (!isActive)
        {
            var sessions = await db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(token);
            foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        }
        Audit(actor, isActive ? "user.reactivated" : "user.deactivated", user.Id, "{}");
        await db.SaveChangesAsync(token);
        return user;
    }

    public async Task<IReadOnlyList<Channel>> ChannelsAsync(CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        return await db.Channels.OrderBy(x => x.DisplayName).ToListAsync(token);
    }

    public async Task<IReadOnlyList<CannedResponseEntity>> CannedResponsesAsync(string? search, CancellationToken token) { var query = db.CannedResponses.AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) { var q = search.ToLower(); query = query.Where(x => x.Title.ToLower().Contains(q) || x.Shortcut.ToLower().Contains(q) || x.Content.ToLower().Contains(q)); } return await query.OrderBy(x => x.Title).ToListAsync(token); }

    public async Task<CannedResponseEntity> AddCannedResponseAsync(string title, string shortcut, string content, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        var item = new CannedResponseEntity { TenantId = actor.TenantId, Title = RequireText(title, "title"), Shortcut = RequireText(shortcut, "shortcut"), Content = RequireText(content, "content") };
        db.CannedResponses.Add(item); Audit(actor, "canned-response.created", item.Id, "{}"); await db.SaveChangesAsync(token); return item;
    }

    public async Task<CannedResponseEntity> UpdateCannedResponseAsync(Guid id, string title, string shortcut, string content, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        var item = await db.CannedResponses.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new InboxException("canned_response_not_found", "The canned response was not found.", 404);
        item.Title = RequireText(title, "title"); item.Shortcut = RequireText(shortcut, "shortcut"); item.Content = RequireText(content, "content");
        Audit(actor, "canned-response.updated", item.Id, "{}"); await db.SaveChangesAsync(token); return item;
    }

    public async Task<bool> DeleteCannedResponseAsync(Guid id, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        var item = await db.CannedResponses.SingleOrDefaultAsync(x => x.Id == id, token);
        if (item is null) return false;
        db.CannedResponses.Remove(item); Audit(actor, "canned-response.deleted", item.Id, "{}"); await db.SaveChangesAsync(token); return true;
    }

    public async Task<IReadOnlyList<NotificationEntity>> NotificationsAsync(bool unreadOnly, CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, token);
        var query = db.Notifications.AsQueryable();
        if (unreadOnly) query = query.Where(x => !x.IsRead);
        return await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
    }

    public async Task<bool> MarkNotificationReadAsync(Guid id, CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, token);
        var item = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id, token);
        if (item is null) return false;
        item.IsRead = true; await db.SaveChangesAsync(token); return true;
    }

    public async Task MarkAllNotificationsReadAsync(CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, token);
        var unread = await db.Notifications.Where(x => !x.IsRead).ToListAsync(token);
        foreach (var item in unread) item.IsRead = true;
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<NotificationPreference>> NotificationPreferencesAsync(CancellationToken cancellationToken)
    {
        var user = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, cancellationToken);
        return await db.NotificationPreferences.Where(x => x.UserId == user.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationPreference>> SetNotificationPreferenceAsync(string kind, bool enabled, CancellationToken cancellationToken)
    {
        var user = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, cancellationToken);
        if (!PreferenceKinds.Contains(kind)) throw new ArgumentException($"Unknown notification kind. Expected one of: {string.Join(", ", PreferenceKinds)}.", nameof(kind));
        var existing = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == user.Id && x.Kind == kind, cancellationToken);
        if (existing is null) db.NotificationPreferences.Add(new NotificationPreference { TenantId = user.TenantId, UserId = user.Id, Kind = kind, Enabled = enabled });
        else existing.Enabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        return await db.NotificationPreferences.Where(x => x.UserId == user.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntryEntity>> AuditAsync(string? search, CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Owner, token);
        var query = db.AuditEntries.AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) { var q = search.ToLower(); query = query.Where(x => x.Action.ToLower().Contains(q) || x.Resource.ToLower().Contains(q)); } return await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(token);
    }

    public async Task<string> AuditCsvAsync(string? search, CancellationToken token)
    {
        var entries = await AuditAsync(search, token);
        var output = new StringBuilder("created_at,actor_id,action,resource,metadata\n");
        foreach (var entry in entries)
            output.Append(FormattableString.Invariant($"{entry.CreatedAt:O},{entry.ActorId},{Csv(entry.Action)},{Csv(entry.Resource)},{Csv(entry.Metadata)}\n"));
        return output.ToString();
    }

    public async Task<OverviewMetrics> OverviewMetricsAsync(int days, CancellationToken token)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        if (days is not (7 or 30 or 90)) throw new ArgumentException("Metrics are available for 7, 30, or 90 days.", nameof(days));
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        return new(
            days,
            since,
            await db.Conversations.CountAsync(x => x.CreatedAt >= since, token),
            await db.Conversations.CountAsync(x => x.Status == ConversationStatus.Open, token),
            await db.Messages.CountAsync(x => x.Direction == MessageDirection.Inbound && x.CreatedAt >= since, token),
            await db.Messages.CountAsync(x => x.Direction == MessageDirection.Outbound && x.CreatedAt >= since, token),
            await db.InternalNotes.CountAsync(x => x.CreatedAt >= since, token));
    }

    public Task<Tenant?> WorkspaceAsync(CancellationToken token) => current.TenantId is { } id ? db.Tenants.SingleOrDefaultAsync(x => x.Id == id, token) : Task.FromResult<Tenant?>(null);

    public async Task<Tenant?> UpdateWorkspaceAsync(string name, int retentionDays, CancellationToken token)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, token);
        var tenant = await WorkspaceAsync(token); if (tenant is null) return null; tenant.Name = name.Trim(); tenant.RetentionDays = Math.Clamp(retentionDays, 30, 3650); Audit(actor, "workspace.updated", tenant.Id, "{}"); await db.SaveChangesAsync(token); return tenant;
    }

    private void Audit(User actor, string action, Guid resource, string metadata) => db.AuditEntries.Add(new AuditEntryEntity { TenantId = actor.TenantId, ActorId = actor.Id, Action = action, Resource = resource.ToString(), Metadata = metadata });
    private static string RequireText(string value, string field) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"A {field} is required.", field) : value.Trim();
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
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

    public async Task<bool> PersistByAssetAsync(string providerAssetId, string providerEventId, byte[] rawBody, CancellationToken token)
    {
        // Never trust tenant/channel ids from webhook input: resolve via the
        // unscoped provider route table keyed by phone_number_id.
        var route = await db.ProviderRoutes.SingleOrDefaultAsync(x => x.Provider == "whatsapp" && x.ProviderAssetId == providerAssetId, token);
        if (route is null) return false;
        return await PersistAsync(route.ChannelId, providerEventId, rawBody, token);
    }
}
