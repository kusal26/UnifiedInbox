namespace UnifiedInbox.Application;

public sealed record GraphPhoneNumber(string Id, string DisplayPhoneNumber, string VerificationStatus);
public sealed record ChannelSummary(Guid Id, string DisplayName, string Platform, string ExternalAccountId, bool IsHealthy, bool IsEnabled, string Status, DateTimeOffset? LastWebhookAt, DateTimeOffset? LastOutboundAt);
public sealed record ConnectionAttemptInfo(Guid AttemptId, string State, DateTimeOffset ExpiresAt);
public sealed record ChannelTestResult(bool Healthy, string Detail);

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
