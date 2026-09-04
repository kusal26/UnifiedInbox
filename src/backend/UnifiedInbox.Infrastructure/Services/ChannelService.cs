using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class ChannelService(InboxDbContext db, ICurrentTenant current, IWhatsAppGraphClient graph, IConfiguration configuration) : IChannelService
{
    private static readonly string[] RequiredScopes = ["whatsapp_business_messaging", "whatsapp_business_management"];

    public async Task<ConnectionAttemptInfo> BeginConnectAsync(string displayName, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
        return await CreateAttemptAsync(actor, null, ConnectionAttemptPurpose.Connect, cancellationToken);
    }

    public async Task<ChannelSummary> CompleteConnectAsync(string state, string nonce, string code, string phoneNumberId, string businessId, string displayName, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(businessId))
            throw new ArgumentException("The signup handshake is incomplete.");
        var attempt = await db.ConnectionAttempts.SingleOrDefaultAsync(x => x.StateHash == Hash(state.Trim()) && x.NonceHash == Hash(nonce.Trim()), cancellationToken);
        if (attempt is null || attempt.TenantId != actor.TenantId || attempt.InitiatingUserId != actor.Id || attempt.ConsumedAt is not null || attempt.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InboxException("invalid_state", "The connection attempt is unknown, expired, or already used.", 400);
        // A reauthorization attempt is bound to one channel: it may only complete for that channel's number.
        if (attempt.ChannelId is { } boundChannelId)
        {
            var bound = await db.Channels.SingleOrDefaultAsync(x => x.Id == boundChannelId && x.ExternalAccountId == phoneNumberId.Trim(), cancellationToken);
            if (bound is null)
                throw new InboxException("invalid_state", "The connection attempt is bound to a different channel.", 400);
        }
        attempt.ConsumedAt = DateTimeOffset.UtcNow; // single-use even when the provider rejects us
        await db.SaveChangesAsync(cancellationToken);

        // Backend-only authorization-code exchange: the app secret never leaves the server.
        var accessToken = await graph.ExchangeCodeAsync(code.Trim(), cancellationToken);
        var scopes = await graph.GetTokenScopesAsync(accessToken, cancellationToken);
        var missing = RequiredScopes.Where(scope => !scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            await AuditAsync(actor, "channel.connect.scopes_missing", attempt.Id, cancellationToken);
            throw new InboxException("scopes_missing", $"The signup did not grant: {string.Join(", ", missing)}.", 422);
        }

        // Prove WABA ownership: the signup-returned phone id must be part of the granted WABA's
        // phone-number collection, present and verified, before we subscribe or persist anything.
        var phones = await graph.GetPhoneNumbersAsync(businessId.Trim(), accessToken, cancellationToken);
        var phone = phones.FirstOrDefault(x => string.Equals(x.Id, phoneNumberId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (phone is null)
            throw new InboxException("phone_not_in_business", "The phone number is not part of the WhatsApp Business Account granted by the signup.", 422);
        if (!string.Equals(phone.VerificationStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(phone.DisplayPhoneNumber))
            throw new InboxException("phone_not_verified", "The phone number is not verified for WhatsApp messaging.", 422);

        try
        {
            // Subscribe the WABA webhook before the channel is marked connected.
            await graph.SubscribeAppAsync(businessId.Trim(), accessToken, cancellationToken);
        }
        catch (InboxException exception)
        {
            await NotifyAsync(actor.TenantId, "channel.unhealthy", "Webhook subscription failed while connecting a channel.", cancellationToken);
            await AuditAsync(actor, "channel.connect.subscription_failed", attempt.Id, cancellationToken);
            throw new InboxException("subscription_failed", exception.Message, 502);
        }

        var existingRoute = await db.ProviderRoutes.SingleOrDefaultAsync(x => x.Provider == "whatsapp" && x.ProviderAssetId == phoneNumberId.Trim(), cancellationToken);
        if (existingRoute is not null && existingRoute.TenantId != actor.TenantId)
            throw new InboxException("asset_already_connected", "This phone number is already connected to another workspace.", 409);
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Platform == "whatsapp" && x.ExternalAccountId == phoneNumberId.Trim(), cancellationToken);
        if (channel is null)
        {
            channel = new Channel(Guid.NewGuid(), actor.TenantId, "whatsapp", phoneNumberId.Trim(), true) { DisplayName = displayName.Trim() };
            db.Channels.Add(channel);
        }
        else
        {
            channel.DisplayName = displayName.Trim();
            channel.IsHealthy = true;
            channel.IsEnabled = true;
            channel.Status = "connected";
        }
        channel.ExternalBusinessId = businessId.Trim();
        var protector = CredentialProtector.FromConfiguration(configuration);
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, cancellationToken);
        var webhookSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); // per-channel verify secret, sealed at rest
        if (credential is null) db.ChannelCredentials.Add(new ChannelCredential { TenantId = actor.TenantId, ChannelId = channel.Id, EncryptedAccessToken = protector.Protect(accessToken), EncryptedWebhookSecret = protector.Protect(webhookSecret) });
        else
        {
            credential.EncryptedAccessToken = protector.Protect(accessToken);
            credential.EncryptedWebhookSecret = protector.Protect(webhookSecret);
            credential.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (existingRoute is null) db.ProviderRoutes.Add(new ProviderRoute { Provider = "whatsapp", ProviderAssetId = phoneNumberId.Trim(), TenantId = actor.TenantId, ChannelId = channel.Id });
        else existingRoute.ChannelId = channel.Id;
        db.ChannelHealth.Add(new ChannelHealth { TenantId = actor.TenantId, ChannelId = channel.Id, IsHealthy = true, Reason = "connected" });
        Emit(actor.TenantId, "channel.updated", channel.Id);
        await AuditAsync(actor, "channel.connected", channel.Id, cancellationToken);
        return ToSummary(channel);
    }

    public async Task<ConnectionAttemptInfo> BeginReauthorizeAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken) ?? throw new InboxException("channel_not_found", "The channel was not found.", 404);
        return await CreateAttemptAsync(actor, channel.Id, ConnectionAttemptPurpose.Reauthorize, cancellationToken);
    }

    public async Task<ChannelTestResult> TestChannelAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken) ?? throw new InboxException("channel_not_found", "The channel was not found.", 404);
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, cancellationToken);
        if (credential is null)
        {
            RecordHealth(channel, false, "missing_credential");
            await db.SaveChangesAsync(cancellationToken);
            return new(false, "No credential is stored for this channel. Reauthorize to continue.");
        }
        string accessToken;
        try { accessToken = CredentialProtector.FromConfiguration(configuration).Unprotect(credential.EncryptedAccessToken); }
        catch (CryptographicException)
        {
            RecordHealth(channel, false, "credential_undecryptable");
            await NotifyAsync(actor.TenantId, "channel.unhealthy", $"Access was revoked for channel {channel.DisplayName}. Reauthorize to continue.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new(false, "The stored credential cannot be decrypted. Rotate keys or reauthorize.");
        }
        try
        {
            var phone = await graph.GetPhoneNumberAsync(channel.ExternalAccountId, accessToken, cancellationToken);
            channel.IsHealthy = true;
            RecordHealth(channel, true, $"verified:{phone.DisplayPhoneNumber}");
            await db.SaveChangesAsync(cancellationToken);
            return new(true, $"Connected as {phone.DisplayPhoneNumber}.");
        }
        catch (InboxException exception) when (exception.Code == "provider_unauthorized")
        {
            channel.IsHealthy = false;
            RecordHealth(channel, false, "provider_unauthorized");
            await NotifyAsync(actor.TenantId, "channel.unhealthy", $"Access was revoked for channel {channel.DisplayName}. Reauthorize to continue.", cancellationToken);
            await AuditAsync(actor, "channel.access_revoked", channel.Id, cancellationToken);
            return new(false, "The provider rejected the stored credential. Reauthorize to continue.");
        }
        catch (InboxException exception)
        {
            RecordHealth(channel, false, "provider_error");
            await db.SaveChangesAsync(cancellationToken);
            return new(false, exception.Message);
        }
    }

    public async Task<IReadOnlyList<ChannelHealth>> HealthHistoryAsync(Guid channelId, CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        if (!await db.Channels.AnyAsync(x => x.Id == channelId, cancellationToken)) throw new InboxException("channel_not_found", "The channel was not found.", 404);
        return await db.ChannelHealth.Where(x => x.ChannelId == channelId).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(cancellationToken);
    }

    public async Task<ChannelSummary> SetEnabledAsync(Guid channelId, bool enabled, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken) ?? throw new InboxException("channel_not_found", "The channel was not found.", 404);
        channel.IsEnabled = enabled;
        channel.Status = enabled ? "connected" : "disabled";
        Emit(actor.TenantId, "channel.updated", channel.Id);
        await AuditAsync(actor, enabled ? "channel.enabled" : "channel.disabled", channel.Id, cancellationToken);
        return ToSummary(channel);
    }

    public async Task DisconnectAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken) ?? throw new InboxException("channel_not_found", "The channel was not found.", 404);
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, cancellationToken);
        if (credential is not null && channel.ExternalBusinessId is not null)
        {
            try
            {
                // Revoke provider access where Graph supports it (WABA webhook subscription removal).
                var accessToken = CredentialProtector.FromConfiguration(configuration).Unprotect(credential.EncryptedAccessToken);
                await graph.UnsubscribeAppAsync(channel.ExternalBusinessId, accessToken, cancellationToken);
            }
            catch (Exception) { /* best effort: local teardown continues regardless */ }
            db.ChannelCredentials.Remove(credential); // access-token and webhook-secret ciphertext are destroyed; history is retained
        }
        else if (credential is not null) db.ChannelCredentials.Remove(credential);
        var routes = await db.ProviderRoutes.Where(x => x.ChannelId == channel.Id).ToListAsync(cancellationToken);
        foreach (var route in routes) db.ProviderRoutes.Remove(route);
        channel.IsEnabled = false;
        channel.IsHealthy = false;
        channel.Status = "disconnected";
        RecordHealth(channel, false, "disconnected");
        await NotifyAsync(actor.TenantId, "channel.unhealthy", $"Channel {channel.DisplayName} was disconnected.", cancellationToken);
        Emit(actor.TenantId, "channel.updated", channel.Id);
        await AuditAsync(actor, "channel.disconnected", channel.Id, cancellationToken);
    }

    public async Task<int> RotateCredentialsAsync(CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Owner, cancellationToken);
        var protector = CredentialProtector.FromConfiguration(configuration);
        var credentials = await db.ChannelCredentials.ToListAsync(cancellationToken);
        var rotated = 0;
        var failures = new List<Guid>();
        foreach (var credential in credentials)
        {
            try
            {
                // Re-seal only envelopes still under the previous key, leaving current ones untouched.
                var tokenNeedsRotation = protector.NeedsRotation(credential.EncryptedAccessToken);
                var secretNeedsRotation = !string.IsNullOrWhiteSpace(credential.EncryptedWebhookSecret) && protector.NeedsRotation(credential.EncryptedWebhookSecret);
                if (!tokenNeedsRotation && !secretNeedsRotation) continue;
                var accessToken = protector.Unprotect(credential.EncryptedAccessToken);
                var webhookSecret = string.IsNullOrWhiteSpace(credential.EncryptedWebhookSecret) ? "" : protector.Unprotect(credential.EncryptedWebhookSecret);
                credential.EncryptedAccessToken = protector.Protect(accessToken);
                if (webhookSecret.Length > 0) credential.EncryptedWebhookSecret = protector.Protect(webhookSecret);
                credential.KeyVersion++;
                credential.UpdatedAt = DateTimeOffset.UtcNow;
                rotated++;
            }
            catch (CryptographicException) { failures.Add(credential.ChannelId); }
        }
        await AuditAsync(actor, "credentials.rotated", actor.TenantId, cancellationToken);
        if (failures.Count > 0) throw new InboxException("credential_rotation_failed", $"{rotated} credentials rotated; {failures.Count} could not be decrypted.", 500);
        return rotated;
    }

    private async Task<ConnectionAttemptInfo> CreateAttemptAsync(User actor, Guid? channelId, ConnectionAttemptPurpose purpose, CancellationToken cancellationToken)
    {
        // Two independent handshake secrets are generated server-side; only their hashes are stored.
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var attempt = new ConnectionAttempt
        {
            TenantId = actor.TenantId,
            ChannelId = channelId,
            InitiatingUserId = actor.Id,
            StateHash = Hash(state),
            NonceHash = Hash(nonce),
            Purpose = purpose,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.ConnectionAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return new(attempt.Id, state, nonce, MetaAppId(), ConfigurationId(), GraphVersion(), EmbeddedSignupVersion(), attempt.ExpiresAt);
    }

    private string MetaAppId() => configuration["WhatsApp:AppId"] ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_ID") ?? "";
    private string ConfigurationId() => configuration["WhatsApp:EmbeddedSignupConfigId"] ?? Environment.GetEnvironmentVariable("WHATSAPP_EMBEDDED_SIGNUP_CONFIG_ID") ?? "";
    private string GraphVersion() => configuration["WhatsApp:GraphVersion"] ?? Environment.GetEnvironmentVariable("WHATSAPP_GRAPH_VERSION") ?? "v23.0";
    private string EmbeddedSignupVersion() => configuration["WhatsApp:EmbeddedSignupVersion"] ?? Environment.GetEnvironmentVariable("WHATSAPP_EMBEDDED_SIGNUP_VERSION") ?? "v4";

    private void RecordHealth(Channel channel, bool healthy, string reason)
    {
        channel.IsHealthy = healthy;
        db.ChannelHealth.Add(new ChannelHealth { TenantId = channel.TenantId, ChannelId = channel.Id, IsHealthy = healthy, Reason = reason });
    }

    private Task AuditAsync(User actor, string action, Guid resource, CancellationToken token)
    {
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = actor.TenantId, ActorId = actor.Id, Action = action, Resource = resource.ToString() });
        return db.SaveChangesAsync(token);
    }

    private async Task NotifyAsync(Guid tenantId, string type, string text, CancellationToken token)
    {
        db.Notifications.Add(new NotificationEntity { TenantId = tenantId, Type = type, Text = text });
        Emit(tenantId, "notification.created", Guid.NewGuid());
        await db.SaveChangesAsync(token);
    }

    private void Emit(Guid tenantId, string type, Guid id) =>
        db.Outbox.Add(new OutboxEvent(Guid.NewGuid(), tenantId, type, JsonSerializer.Serialize(new { id }), DateTimeOffset.UtcNow));

    private static ChannelSummary ToSummary(Channel channel) =>
        new(channel.Id, channel.DisplayName, channel.Platform, channel.ExternalAccountId, channel.IsHealthy, channel.IsEnabled, channel.Status, channel.LastWebhookAt, channel.LastOutboundAt);

    /// <summary>Hash helper for connection states and nonces (the raw values never touch the database).</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
