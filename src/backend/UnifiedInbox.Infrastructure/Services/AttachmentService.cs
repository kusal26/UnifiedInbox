using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AttachmentService(InboxDbContext db, ICurrentTenant current, IObjectStorage storage, IAttachmentScanner scanner, IHostEnvironment environment) : IAttachmentService
{
    private static readonly TimeSpan UploadTimeToLive = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadTimeToLive = TimeSpan.FromMinutes(5);

    public async Task<StagedAttachmentResponse> StageAsync(string fileName, string contentType, long size, CancellationToken token)
    {
        var validated = AttachmentPolicy.Validate(fileName, contentType, size);
        EnsureExtensionMatchesType(validated.FileName, validated.ContentType);
        if (current.TenantId is not { } tenantId || current.UserId is not { } userId) throw new UnauthorizedAccessException();
        var item = new Attachment { TenantId = tenantId, UploaderId = userId, FileName = validated.FileName, ContentType = validated.ContentType, Size = validated.Size, ObjectKey = $"{tenantId:N}/{Guid.NewGuid():N}/{validated.FileName}", ExpiresAt = DateTimeOffset.UtcNow.Add(UploadTimeToLive) };
        db.Attachments.Add(item);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "attachment.staged", Resource = item.Id.ToString() });
        await db.SaveChangesAsync(token);
        // Direct-to-MinIO: the client PUTs bytes to this URL; the API stays out of the byte path.
        var uploadUrl = await storage.PresignedPutAsync(item.ObjectKey, item.ContentType, UploadTimeToLive, token);
        return new(item.Id, item.FileName, item.ContentType, item.Size, item.ExpiresAt, item.ObjectKey, uploadUrl);
    }

    public async Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (current.TenantId is not { } tenantId || current.UserId is not { } userId) throw new UnauthorizedAccessException();
        var item = await db.Attachments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return false; // fail-closed tenant filter already applied
        if (item.Status != AttachmentStatus.Staged || item.MessageId is not null) throw new InboxException("attachment_already_claimed", "The attachment was already claimed.", 409);
        if (item.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            item.Status = AttachmentStatus.Rejected;
            await db.SaveChangesAsync(cancellationToken);
            throw new InboxException("attachment_expired", "The staging record has expired.", 410);
        }
        EnsureExtensionMatchesType(item.FileName, item.ContentType);
        var stored = await storage.StatAsync(item.ObjectKey, cancellationToken);
        if (stored is null) throw new InboxException("attachment_upload_incomplete", "No bytes were uploaded for this staging record.", 422);
        if (stored.Size != item.Size || stored.Size <= 0 || stored.Size > AttachmentPolicy.MaximumBytes)
        {
            await RejectAsync(item, cancellationToken);
            throw new InboxException("attachment_size_mismatch", "The uploaded bytes do not match the staged size.", 400);
        }
        await using var content = await storage.OpenReadAsync(item.ObjectKey, cancellationToken);
        var head = new byte[4096];
        var headLength = await content.ReadAsync(head, cancellationToken);
        if (!AttachmentSniffer.Matches(item.ContentType, head.AsSpan(0, headLength)))
        {
            await RejectAsync(item, cancellationToken);
            throw new InboxException("malicious_attachment", "The uploaded bytes do not match the declared content type.", 400);
        }
        await ScanAsync(item, content, cancellationToken);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "attachment.completed", Resource = item.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AttachmentDownload?> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (current.TenantId is not { } || current.UserId is not { }) throw new UnauthorizedAccessException();
        var item = await db.Attachments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return null; // fail-closed tenant filter already applied
        if (item.Status is AttachmentStatus.Expired or AttachmentStatus.Rejected) return null;
        if (item.ExpiresAt <= DateTimeOffset.UtcNow && item.Status == AttachmentStatus.Staged) return null;
        var url = await storage.PresignedGetAsync(item.ObjectKey, DownloadTimeToLive, cancellationToken);
        return new(url, item.ContentType, item.FileName, DateTimeOffset.UtcNow.Add(DownloadTimeToLive));
    }

    /// <summary>Deletes expired staging records, unclaimed objects, and orphaned keys. Called by the scheduled cleanup worker.</summary>
    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await db.Attachments.Where(x => x.Status == AttachmentStatus.Staged && x.ExpiresAt <= now).ToListAsync(cancellationToken);
        foreach (var item in stale)
        {
            item.Status = AttachmentStatus.Expired;
            try { await storage.DeleteAsync(item.ObjectKey, cancellationToken); } catch { /* best effort */ }
        }
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    private async Task ScanAsync(Attachment item, Stream content, CancellationToken cancellationToken)
    {
        if (!scanner.IsConfigured)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Test")) return;
            throw new InboxException("attachment_scan_unavailable", "Attachment scanning is unavailable.", 503);
        }
        AttachmentScanResult result;
        try { result = await scanner.ScanAsync(content, cancellationToken); }
        catch (Exception)
        {
            // In Development/Test a refused scanner connection is treated as
            // unavailable-but-open so local flows work without ClamAV; other
            // environments stay fail-closed.
            if (environment.IsDevelopment() || environment.IsEnvironment("Test")) return;
            throw new InboxException("attachment_scan_unavailable", "Attachment scanning is unavailable.", 503);
        }
        if (result.Outcome == AttachmentScanOutcome.Infected)
        {
            await RejectAsync(item, cancellationToken);
            throw new InboxException("malicious_attachment", $"The uploaded file was rejected by malware scanning{(result.ThreatName is null ? "." : $": {result.ThreatName}.")}", 400);
        }
    }

    private async Task RejectAsync(Attachment item, CancellationToken cancellationToken)
    {
        item.Status = AttachmentStatus.Rejected;
        try { await storage.DeleteAsync(item.ObjectKey, cancellationToken); } catch { /* best effort */ }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureExtensionMatchesType(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var allowed = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => new[] { ".jpg", ".jpeg" },
            "image/png" => new[] { ".png" },
            "image/gif" => new[] { ".gif" },
            "image/webp" => new[] { ".webp" },
            "application/pdf" => new[] { ".pdf" },
            "video/mp4" => new[] { ".mp4" },
            _ => Array.Empty<string>()
        };
        if (!allowed.Contains(extension)) throw new InboxException("malicious_attachment", "File extension does not match its content type.", 400);
    }
}
