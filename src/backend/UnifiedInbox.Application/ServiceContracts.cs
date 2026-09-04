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
public sealed class InboxException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public interface IMailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken);
}

public sealed record SessionInfo(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
public interface IAuthService
{
    Task<AuthTokens?> LoginAsync(string tenantSlug, string email, string password, CancellationToken cancellationToken);
    Task RegisterAsync(Registration registration, CancellationToken cancellationToken);
    Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken);
    Task<bool> ResendVerificationAsync(string email, CancellationToken cancellationToken);
    Task<bool> ForgotPasswordAsync(string email, CancellationToken cancellationToken);
    Task<bool> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken);
    Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionInfo>> SessionsAsync(CancellationToken cancellationToken);
    Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task RevokeAllSessionsAsync(CancellationToken cancellationToken);
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
    Task<ActivityItem?> SendAsync(Guid id, string body, string idempotencyKey, string? templateName, IReadOnlyList<Guid>? attachmentIds, CancellationToken cancellationToken);
    Task<ConversationSummary?> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken cancellationToken);
    Task<ConversationSummary?> MarkReadAsync(Guid id, long throughSequence, CancellationToken cancellationToken);
    Task<bool> UpdateCustomerNotesAsync(Guid id, string? notes, CancellationToken cancellationToken);
}

public interface IAdministrationService
{
    Task<IReadOnlyList<User>> UsersAsync(CancellationToken cancellationToken);
    Task<User> SetUserRoleAsync(Guid userId, UserRole role, CancellationToken cancellationToken);
    Task<User> SetUserActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken);
    Task<IReadOnlyList<Channel>> ChannelsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CannedResponseEntity>> CannedResponsesAsync(string? search, CancellationToken cancellationToken);
    Task<CannedResponseEntity> AddCannedResponseAsync(string title, string shortcut, string content, CancellationToken cancellationToken);
    Task<CannedResponseEntity> UpdateCannedResponseAsync(Guid id, string title, string shortcut, string content, CancellationToken cancellationToken);
    Task<bool> DeleteCannedResponseAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationEntity>> NotificationsAsync(bool unreadOnly, CancellationToken cancellationToken);
    Task<bool> MarkNotificationReadAsync(Guid id, CancellationToken cancellationToken);
    Task MarkAllNotificationsReadAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationPreference>> NotificationPreferencesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationPreference>> SetNotificationPreferenceAsync(string kind, bool enabled, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntryEntity>> AuditAsync(string? search, CancellationToken cancellationToken);
    Task<string> AuditCsvAsync(string? search, CancellationToken cancellationToken);
    Task<OverviewMetrics> OverviewMetricsAsync(int days, CancellationToken cancellationToken);
    Task<Tenant?> WorkspaceAsync(CancellationToken cancellationToken);
    Task<Tenant?> UpdateWorkspaceAsync(string name, int retentionDays, CancellationToken cancellationToken);
}

public sealed record OverviewMetrics(int Days, DateTimeOffset Since, long ConversationsOpened, long OpenConversations, long MessagesInbound, long MessagesOutbound, long NotesCreated);

public sealed record InvitationSummary(Guid Id, string Email, UserRole Role, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public interface IInvitationService
{
    Task<IReadOnlyList<InvitationSummary>> ListAsync(CancellationToken cancellationToken);
    Task<InvitationSummary> InviteAsync(string email, UserRole role, CancellationToken cancellationToken);
    Task<bool> AcceptAsync(string token, string displayName, string password, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken);
}

public interface IWebhookService
{
    Task<bool> PersistAsync(Guid channelId, string providerEventId, byte[] rawBody, CancellationToken cancellationToken);
    Task<bool> PersistByAssetAsync(string providerAssetId, string providerEventId, byte[] rawBody, CancellationToken cancellationToken);
}

public sealed record StagedAttachmentResponse(Guid Id, string FileName, string ContentType, long Size, DateTimeOffset ExpiresAt, string ObjectKey, string UploadUrl);
public sealed record AttachmentDownload(string DownloadUrl, string ContentType, string FileName, DateTimeOffset ExpiresAt);
public interface IAttachmentService
{
    Task<StagedAttachmentResponse> StageAsync(string fileName, string contentType, long size, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken);
    Task<AttachmentDownload?> DownloadAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>Atomically binds distinct, Ready, unexpired, tenant-owned attachments to a message.</summary>
    Task ClaimForMessageAsync(Guid messageId, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken);
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken);
}
