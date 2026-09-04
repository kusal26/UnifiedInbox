using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

/// <summary>
/// Approved-template catalog for a WhatsApp channel. Templates are fetched live from the WABA with
/// the channel's own token and sanitized before they are surfaced; send acceptance uses the same
/// catalog so an unapproved or incorrectly parameterized template is rejected before persistence.
/// </summary>
public sealed class WhatsAppTemplateService(InboxDbContext db, ICurrentTenant current, IWhatsAppGraphClient graph, IConfiguration configuration) : IWhatsAppTemplateService
{
    public async Task<IReadOnlyList<WhatsAppTemplateInfo>> ApprovedAsync(Guid channelId, CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, cancellationToken);
        var access = await ResolveChannelAccessAsync(channelId, cancellationToken);
        if (access is null) return [];
        var templates = await ListTemplatesAsync(access.Value, cancellationToken);
        return templates.Where(x => string.Equals(x.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task ValidateAsync(Guid channelId, OutboundTemplate template, CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Agent, cancellationToken);
        var access = await ResolveChannelAccessAsync(channelId, cancellationToken);
        if (access is null) throw new InboxException("channel_authorization_expired", "This channel is not authorized to send templates. Reauthorize to continue.", 502);
        var templates = await ListTemplatesAsync(access.Value, cancellationToken);
        var approved = templates.SingleOrDefault(x => string.Equals(x.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, template.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Language, template.Language, StringComparison.OrdinalIgnoreCase));
        if (approved is null)
            throw new InboxException("template_invalid", $"The template \"{template.Name}\" is not approved for this channel.", 422);
        EnsureMatchesApprovedSchema(template, approved);
    }

    private async Task<(string BusinessId, string AccessToken)?> ResolveChannelAccessAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await db.Channels.SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken)
            ?? throw new InboxException("channel_not_found", "The channel was not found.", 404);
        if (string.IsNullOrWhiteSpace(channel.ExternalBusinessId) || !channel.IsEnabled) return null;
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, cancellationToken);
        if (credential is null) return null;
        try { return (channel.ExternalBusinessId, BuildProtector().Unprotect(credential.EncryptedAccessToken)); }
        catch (CryptographicException) { return null; }
    }

    private async Task<IReadOnlyList<WhatsAppTemplateInfo>> ListTemplatesAsync((string BusinessId, string AccessToken) access, CancellationToken cancellationToken)
    {
        try { return await graph.ListMessageTemplatesAsync(access.BusinessId, access.AccessToken, cancellationToken); }
        catch (InboxException exception) when (exception.Code == "provider_unauthorized")
        {
            throw new InboxException("channel_authorization_expired", "The channel credential was rejected by WhatsApp. Reauthorize to continue.", 502);
        }
    }

    private static void EnsureMatchesApprovedSchema(OutboundTemplate template, WhatsAppTemplateInfo approved)
    {
        if (template.Components is null) return;
        foreach (var component in template.Components)
        {
            var type = component.TryGetProperty("type", out var rawType) && rawType.ValueKind == JsonValueKind.String
                ? rawType.GetString()!.ToUpperInvariant()
                : "BODY";
            var count = 0;
            if (component.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array) count = parameters.GetArrayLength();
            var schema = approved.Components.FirstOrDefault(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase));
            if (schema is null)
                throw new InboxException("template_invalid", $"The template does not accept {type} parameters.", 422);
            if (type is "BODY" or "HEADER" && schema.ParameterCount != count)
                throw new InboxException("template_invalid", $"The template requires {schema.ParameterCount} {type} parameter(s) but {count} were supplied.", 422);
        }
    }

    private CredentialProtector BuildProtector()
    {
        var active = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_MASTER_KEY") ?? throw new InvalidOperationException("Credentials:MasterKey is required."));
        var previousRaw = configuration["Credentials:PreviousMasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_PREVIOUS_MASTER_KEY");
        return new CredentialProtector(active, string.IsNullOrWhiteSpace(previousRaw) ? null : Convert.FromBase64String(previousRaw));
    }
}
