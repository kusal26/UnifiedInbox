namespace UnifiedInbox.Application;

public sealed record ValidatedAttachment(string FileName, string ContentType, long Size);

public static class AttachmentPolicy
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf", "video/mp4" };
    public static ValidatedAttachment Validate(string fileName, string contentType, long size)
    {
        if (size <= 0 || size > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(size), "Attachments must be between 1 byte and 10 MB.");
        if (!AllowedTypes.Contains(contentType)) throw new ArgumentException("Unsupported attachment type.", nameof(contentType));
        var sanitized = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(sanitized)) throw new ArgumentException("A file name is required.", nameof(fileName));
        return new(sanitized, contentType.ToLowerInvariant(), size);
    }
}
