using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AttachmentService(InboxDbContext db, ICurrentTenant current, IConfiguration configuration, IHostEnvironment environment) : IAttachmentService
{
    public async Task<StagedAttachmentResponse> StageAsync(string fileName, string contentType, long size, CancellationToken token)
    {
        var validated = AttachmentPolicy.Validate(fileName, contentType, size);
        EnsureExtensionMatchesType(validated.FileName, validated.ContentType);
        if (current.TenantId is not { } tenantId || current.UserId is not { } userId) throw new UnauthorizedAccessException();
        var item = new Attachment { TenantId = tenantId, UploaderId = userId, FileName = validated.FileName, ContentType = validated.ContentType, Size = validated.Size, ObjectKey = $"{tenantId:N}/{Guid.NewGuid():N}/{validated.FileName}", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
        db.Attachments.Add(item); db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "attachment.staged", Resource = item.Id.ToString() });
        await db.SaveChangesAsync(token);
        // Direct-to-MinIO presigned PUT is wired in the storage milestone; until then the
        // client uploads bytes to the content endpoint which persists to the object store.
        return new(item.Id, item.FileName, item.ContentType, item.Size, item.ExpiresAt, item.ObjectKey, $"/api/v1/attachments/{item.Id}/content");
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
        await ScanAsync(item, cancellationToken);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "attachment.completed", Resource = item.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<(byte[] Content, string ContentType, string FileName)?> DownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        // Object-byte storage (MinIO) is wired in the storage milestone; metadata checks
        // stay fail-closed until then.
        if (current.TenantId is not { } || current.UserId is not { }) throw new UnauthorizedAccessException();
        return Task.FromResult<(byte[] Content, string ContentType, string FileName)?>(null);
    }

    private Task ScanAsync(Attachment item, CancellationToken cancellationToken)
    {
        var clamav = configuration["ClamAv:Host"] ?? Environment.GetEnvironmentVariable("CLAMAV_HOST");
        if (string.IsNullOrWhiteSpace(clamav) && !(environment.IsDevelopment() || environment.IsEnvironment("Test")))
            throw new InboxException("attachment_scan_unavailable", "Attachment scanning is unavailable.", 503);
        // Byte-level magic-byte + ClamAV scan runs once object bytes are persisted to MinIO.
        return Task.CompletedTask;
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
