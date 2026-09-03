using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AttachmentService(InboxDbContext db, ICurrentTenant current) : IAttachmentService
{
    public async Task<StagedAttachmentResponse> StageAsync(string fileName, string contentType, long size, CancellationToken token)
    {
        var validated = AttachmentPolicy.Validate(fileName, contentType, size);
        if (current.TenantId is not { } tenantId || current.UserId is not { } userId) throw new UnauthorizedAccessException();
        var item = new Attachment { TenantId = tenantId, UploaderId = userId, FileName = validated.FileName, ContentType = validated.ContentType, Size = validated.Size, ObjectKey = $"{tenantId:N}/{Guid.NewGuid():N}/{validated.FileName}", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15) };
        db.Attachments.Add(item); db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "attachment.staged", Resource = item.Id.ToString() });
        await db.SaveChangesAsync(token); return new(item.Id, item.FileName, item.ContentType, item.Size, item.ExpiresAt, item.ObjectKey);
    }
}
