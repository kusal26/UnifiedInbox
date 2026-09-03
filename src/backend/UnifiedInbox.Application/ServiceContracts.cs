using UnifiedInbox.Domain;

namespace UnifiedInbox.Application;

public interface ICurrentTenant
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    UserRole? Role { get; }
}

public sealed record AuthTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt);
public sealed record CurrentUser(Guid Id, Guid TenantId, string Email, string DisplayName, UserRole Role, string WorkspaceName);
public sealed record Registration(string WorkspaceName, string WorkspaceSlug, string DisplayName, string Email, string Password);
public interface ITokenIssuer { (string Token, DateTimeOffset ExpiresAt) Issue(User user); }
public interface IAuthService
{
    Task<AuthTokens?> LoginAsync(string tenantSlug, string email, string password, CancellationToken cancellationToken);
    Task<AuthTokens> RegisterAsync(Registration registration, CancellationToken cancellationToken);
    Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);
    Task<CurrentUser?> MeAsync(CancellationToken cancellationToken);
}

public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, string? NextCursor);
public sealed record ConversationDetails(Guid Id, ConversationStatus Status, DateTimeOffset UpdatedAt, long LastReadSequence, Guid ChannelId, string Platform, Guid ContactId, string ContactName, string Phone, string? Email, string? CustomerNotes);
public interface IInboxService
{
    Task<ConversationPage> ListAsync(string? search, ConversationStatus? status, string? channel, bool unreadOnly, string? cursor, int pageSize, CancellationToken cancellationToken);
    Task<ConversationDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ActivityResponse?> ActivityAsync(Guid id, long? before, int pageSize, CancellationToken cancellationToken);
    Task<ActivityItem?> AddNoteAsync(Guid id, string body, CancellationToken cancellationToken);
    Task<ActivityItem?> SendAsync(Guid id, string body, string idempotencyKey, CancellationToken cancellationToken);
    Task<ConversationSummary?> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken cancellationToken);
    Task<ConversationSummary?> MarkReadAsync(Guid id, long throughSequence, CancellationToken cancellationToken);
    Task<bool> UpdateCustomerNotesAsync(Guid id, string? notes, CancellationToken cancellationToken);
}

public interface IAdministrationService
{
    Task<IReadOnlyList<User>> UsersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Channel>> ChannelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CannedResponseEntity>> CannedResponsesAsync(string? search, CancellationToken cancellationToken);
    Task<CannedResponseEntity> AddCannedResponseAsync(string title, string shortcut, string content, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationEntity>> NotificationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntryEntity>> AuditAsync(string? search, CancellationToken cancellationToken);
    Task<Tenant?> WorkspaceAsync(CancellationToken cancellationToken);
    Task<Tenant?> UpdateWorkspaceAsync(string name, int retentionDays, CancellationToken cancellationToken);
}

public interface IWebhookService
{
    Task<bool> PersistAsync(Guid channelId, string providerEventId, byte[] rawBody, CancellationToken cancellationToken);
}

public sealed record StagedAttachmentResponse(Guid Id, string FileName, string ContentType, long Size, DateTimeOffset ExpiresAt, string ObjectKey);
public interface IAttachmentService
{
    Task<StagedAttachmentResponse> StageAsync(string fileName, string contentType, long size, CancellationToken cancellationToken);
}
