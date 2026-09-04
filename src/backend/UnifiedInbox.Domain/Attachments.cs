namespace UnifiedInbox.Domain;

// Numeric values are persisted, so new states are appended and existing values preserved.
public enum AttachmentStatus { Staged = 0, Claimed = 1, Expired = 2, Rejected = 3, Ready = 4 }

public sealed class Attachment : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UploaderId { get; set; }
    public Guid? MessageId { get; set; } public string ObjectKey { get; set; } = ""; public string FileName { get; set; } = "";
    public string ContentType { get; set; } = ""; public long Size { get; set; } public AttachmentStatus Status { get; set; } = AttachmentStatus.Staged;
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Set when upload length, magic bytes, extension, and malware checks all pass.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>The content type confirmed by magic-byte sniffing once the object is Ready.</summary>
    public string? DetectedContentType { get; set; }
}
