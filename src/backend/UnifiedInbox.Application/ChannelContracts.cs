namespace UnifiedInbox.Application;

using UnifiedInbox.Application.Messaging;

public sealed record GraphPhoneNumber(string Id, string DisplayPhoneNumber, string VerificationStatus);
public sealed record ChannelSummary(Guid Id, string DisplayName, string Platform, string ExternalAccountId, bool IsHealthy, bool IsEnabled, string Status, DateTimeOffset? LastWebhookAt, DateTimeOffset? LastOutboundAt);
public sealed record ConnectionAttemptInfo(Guid AttemptId, string State, DateTimeOffset ExpiresAt);
public sealed record ChannelTestResult(bool Healthy, string Detail);

/// <summary>A sanitized approved-template shape. Never carries tokens or raw Graph responses.</summary>
public sealed record WhatsAppTemplateInfo(string Name, string Language, string Category, string Status, IReadOnlyList<WhatsAppTemplateComponentInfo> Components);
/// <summary>The provider component type (BODY/HEADER/FOOTER/BUTTONS) and the number of parameters
/// the approved template requires for it (derived from placeholders/media format).</summary>
public sealed record WhatsAppTemplateComponentInfo(string Type, int ParameterCount);
/// <summary>Authenticated media metadata used to download inbound bytes privately.</summary>
public sealed record GraphMediaMetadata(string Url, string MimeType, long? FileSize);

/// <summary>Minimal Meta Graph API surface for WhatsApp onboarding. Implemented against the configured Graph version.</summary>
public interface IWhatsAppGraphClient
{
    /// <summary>Exchanges an Embedded Signup authorization code for a system-user access token (backend only).</summary>
    Task<string> ExchangeCodeAsync(string code, CancellationToken cancellationToken);
    Task<GraphPhoneNumber> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken);
    Task<string> GetBusinessNameAsync(string businessId, string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetTokenScopesAsync(string accessToken, CancellationToken cancellationToken);
    Task SubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken);
    Task UnsubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken);
    /// <summary>Lists the WABA's message templates. The caller decides status filtering and sanitization.</summary>
    Task<IReadOnlyList<WhatsAppTemplateInfo>> ListMessageTemplatesAsync(string businessId, string accessToken, CancellationToken cancellationToken);
    /// <summary>Resolves authenticated media metadata (download url, mime type, size) for a media id.</summary>
    Task<GraphMediaMetadata> GetMediaAsync(string mediaId, string accessToken, CancellationToken cancellationToken);
}

public interface IChannelService
{
    Task<ConnectionAttemptInfo> BeginConnectAsync(string displayName, CancellationToken cancellationToken);
    Task<ChannelSummary> CompleteConnectAsync(string state, string code, string phoneNumberId, string businessId, string displayName, CancellationToken cancellationToken);
    Task<ConnectionAttemptInfo> BeginReauthorizeAsync(Guid channelId, CancellationToken cancellationToken);
    Task<ChannelTestResult> TestChannelAsync(Guid channelId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Domain.ChannelHealth>> HealthHistoryAsync(Guid channelId, CancellationToken cancellationToken);
    Task<ChannelSummary> SetEnabledAsync(Guid channelId, bool enabled, CancellationToken cancellationToken);
    Task DisconnectAsync(Guid channelId, CancellationToken cancellationToken);
    Task<int> RotateCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>Approved-template catalog for a WhatsApp channel. Only sanitized, approved templates are
/// ever surfaced, and send acceptance rejects unapproved or incorrectly parameterized templates.</summary>
public interface IWhatsAppTemplateService
{
    Task<IReadOnlyList<WhatsAppTemplateInfo>> ApprovedAsync(Guid channelId, CancellationToken cancellationToken);
    /// <summary>Throws <c>template_invalid</c> when the requested template is not approved for the
    /// channel or its parameterization does not match the approved schema.</summary>
    Task ValidateAsync(Guid channelId, OutboundTemplate template, CancellationToken cancellationToken);
}
