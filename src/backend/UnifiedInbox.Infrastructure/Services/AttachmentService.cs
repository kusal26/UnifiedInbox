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
    /// <summary>Upper bound for one cleanup pass so a single tenant cannot starve the worker.</summary>
    private const int CleanupBatchSize = 200;

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
        if (item.Status != AttachmentStatus.Staged || item.MessageId is not null) throw new InboxException("attachment_already_claimed", "The attachment was already completed or claimed.", 409);
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
        // Everything passed: record the scanned, ready object in the same save that completes it.
        item.Status = AttachmentStatus.Ready;
        item.DetectedContentType = item.ContentType;
        item.CompletedAt = DateTimeOffset.UtcNow;
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
        // Bytes uploaded against a staging record but never scanned/completed are never downloadable.
        if (item.Status == AttachmentStatus.Staged) return null;
        if (item.Status == AttachmentStatus.Ready && item.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var url = await storage.PresignedGetAsync(item.ObjectKey, DownloadTimeToLive, cancellationToken);
        return new(url, item.ContentType, item.FileName, DateTimeOffset.UtcNow.Add(DownloadTimeToLive));
    }

    /// <summary>Expires one bounded batch of stale staging records per call. Called by the
    /// scheduled cleanup worker inside a per-tenant execution scope; the caller repeats the
    /// call until it returns zero so every tenant is drained in bounded passes.</summary>
    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await db.Attachments
            .Where(x => x.ExpiresAt <= now && (x.Status == AttachmentStatus.Staged || (x.Status == AttachmentStatus.Ready && x.MessageId == null)))
            .OrderBy(x => x.ExpiresAt).Take(CleanupBatchSize).ToListAsync(cancellationToken);
        foreach (var item in stale)
        {
            item.Status = AttachmentStatus.Expired;
            try { await storage.DeleteAsync(item.ObjectKey, cancellationToken); } catch { /* best effort */ }
        }
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    /// <summary>
    /// Atomically binds distinct, scanned (<see cref="AttachmentStatus.Ready"/>), unexpired,
    /// tenant-owned attachments to a message. Each attachment is claimed with a conditional
    /// update (status Ready + unclaimed + unexpired) inside the ambient/owning transaction, so
    /// two concurrent sends yield exactly one winner and one <c>attachment_already_claimed</c>.
    /// </summary>
    public async Task ClaimForMessageAsync(Guid messageId, IReadOnlyList<Guid> attachmentIds, CancellationToken token)
    {
        if (current.TenantId is null) throw new UnauthorizedAccessException();
        if (attachmentIds.Count == 0) return;
        var distinct = attachmentIds.Distinct().ToList();
        if (distinct.Count != attachmentIds.Count) throw new InboxException("attachment_already_claimed", "Duplicate attachment ids were supplied.", 409);

        var now = DateTimeOffset.UtcNow;
        var found = await db.Attachments
            .Where(x => distinct.Contains(x.Id))
            .Select(x => new AttachmentState(x.Id, x.Status, x.MessageId, x.ExpiresAt))
            .ToListAsync(token);
        if (found.Count != distinct.Count) throw new InboxException("attachment_not_found", "One or more attachments were not found.", 404);
        if (found.Any(x => x.Status != AttachmentStatus.Ready || x.MessageId is not null))
            throw new InboxException("attachment_already_claimed", "An attachment was already claimed or is not ready for sending.", 409);
        if (found.Any(x => x.ExpiresAt <= now)) throw new InboxException("attachment_expired", "An attachment staging record has expired.", 410);

        foreach (var id in distinct)
        {
            var claimed = await db.Attachments
                .Where(x => x.Id == id && x.Status == AttachmentStatus.Ready && x.MessageId == null && x.ExpiresAt > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.MessageId, messageId)
                    .SetProperty(x => x.Status, AttachmentStatus.Claimed), token);
            if (claimed == 0) throw new InboxException("attachment_already_claimed", "An attachment was already claimed by another send.", 409);
        }
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

    private sealed record AttachmentState(Guid Id, AttachmentStatus Status, Guid? MessageId, DateTimeOffset ExpiresAt);
}
