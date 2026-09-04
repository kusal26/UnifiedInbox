namespace UnifiedInbox.Domain;

public enum AttachmentStatus { Staged, Claimed, Expired, Rejected }

public sealed class Attachment : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UploaderId { get; set; }
    public Guid? MessageId { get; set; } public string ObjectKey { get; set; } = ""; public string FileName { get; set; } = "";
    public string ContentType { get; set; } = ""; public long Size { get; set; } public AttachmentStatus Status { get; set; } = AttachmentStatus.Staged;
    public DateTimeOffset ExpiresAt { get; set; }
}
