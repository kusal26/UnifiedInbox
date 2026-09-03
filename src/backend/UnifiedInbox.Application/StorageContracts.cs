namespace UnifiedInbox.Application;

/// <summary>Object-byte store for attachment payloads. The API never serves bytes itself.</summary>
public interface IObjectStorage
{
    /// <summary>Creates a short-lived upload URL the client PUTs bytes to directly.</summary>
    Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Creates a short-lived download URL after ownership checks passed.</summary>
    Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken);

    /// <summary>Opens the stored bytes for server-side verification (magic bytes, malware scan).</summary>
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Returns stored size/content-type, or null when the key does not exist.</summary>
    Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record StoredObjectInfo(long Size, string? ContentType);

public enum AttachmentScanOutcome { Clean, Infected }

public sealed record AttachmentScanResult(AttachmentScanOutcome Outcome, string? ThreatName);

public interface IAttachmentScanner
{
    /// <summary>True when a scanner endpoint is configured and reachable for use.</summary>
    bool IsConfigured { get; }

    Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Magic-byte verification so declared MIME types cannot be spoofed by extension alone.</summary>
public static class AttachmentSniffer
{
    public static bool Matches(string contentType, ReadOnlySpan<byte> head)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
            "image/png" => head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A,
            "image/gif" => head.Length >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38 && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61,
            "image/webp" => head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50,
            "application/pdf" => head.Length >= 4 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46,
            "video/mp4" => head.Length >= 12 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70,
            _ => false,
        };
    }
}
