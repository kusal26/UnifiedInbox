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

    public async Task<ChannelSummary> CompleteConnectAsync(string state, string code, string phoneNumberId, string businessId, string displayName, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(businessId))
            throw new ArgumentException("The signup handshake is incomplete.");
        var attempt = await db.ConnectionAttempts.SingleOrDefaultAsync(x => x.StateHash == Hash(state.Trim()), cancellationToken);
        if (attempt is null || attempt.TenantId != actor.TenantId || attempt.InitiatingUserId != actor.Id || attempt.ConsumedAt is not null || attempt.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InboxException("invalid_state", "The connection attempt is unknown, expired, or already used.", 400);
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
        var phone = await graph.GetPhoneNumberAsync(phoneNumberId.Trim(), accessToken, cancellationToken);
        if (!string.Equals(phone.VerificationStatus, "VERIFIED", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(phone.DisplayPhoneNumber))
            throw new InboxException("phone_not_verified", "The phone number is not verified for WhatsApp messaging.", 422);
        await graph.GetBusinessNameAsync(businessId.Trim(), accessToken, cancellationToken);
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
            throw new InboxException("number_in_use", "This phone number is already connected to another workspace.", 409);
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
        var protector = BuildProtector();
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, cancellationToken);
        if (credential is null) db.ChannelCredentials.Add(new ChannelCredential { TenantId = actor.TenantId, ChannelId = channel.Id, EncryptedAccessToken = protector.Protect(accessToken) });
        else { credential.EncryptedAccessToken = protector.Protect(accessToken); credential.UpdatedAt = DateTimeOffset.UtcNow; }
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
        try { accessToken = BuildProtector().Unprotect(credential.EncryptedAccessToken); }
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
                var accessToken = BuildProtector().Unprotect(credential.EncryptedAccessToken);
                await graph.UnsubscribeAppAsync(channel.ExternalBusinessId, accessToken, cancellationToken);
            }
            catch (Exception) { /* best effort: local teardown continues regardless */ }
            db.ChannelCredentials.Remove(credential); // ciphertext is destroyed; history is retained
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
        var protector = BuildProtector();
        var credentials = await db.ChannelCredentials.ToListAsync(cancellationToken);
        var rotated = 0;
        var failures = new List<Guid>();
        foreach (var credential in credentials)
        {
            try
            {
                var plaintext = protector.Unprotect(credential.EncryptedAccessToken);
                credential.EncryptedAccessToken = protector.Protect(plaintext);
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
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var attempt = new ConnectionAttempt
        {
            TenantId = actor.TenantId,
            ChannelId = channelId,
            InitiatingUserId = actor.Id,
            StateHash = Hash(state),
            Purpose = purpose,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.ConnectionAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return new(attempt.Id, state, attempt.ExpiresAt);
    }

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

    private CredentialProtector BuildProtector()
    {
        var active = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_MASTER_KEY") ?? throw new InvalidOperationException("Credentials:MasterKey is required."));
        var previousRaw = configuration["Credentials:PreviousMasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_PREVIOUS_MASTER_KEY");
        return new CredentialProtector(active, string.IsNullOrWhiteSpace(previousRaw) ? null : Convert.FromBase64String(previousRaw));
    }

    private static ChannelSummary ToSummary(Channel channel) =>
        new(channel.Id, channel.DisplayName, channel.Platform, channel.ExternalAccountId, channel.IsHealthy, channel.IsEnabled, channel.Status, channel.LastWebhookAt, channel.LastOutboundAt);

    /// <summary>Hash helper for connection states (the raw state never touches the database).</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));}
