using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

public sealed class AttachmentServiceTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00];
    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A];

    [Fact]
    public async Task Stage_returns_a_direct_upload_url_and_complete_verifies_bytes()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        staged.UploadUrl.ShouldStartWith("https://storage.test/");
        staged.ObjectKey.ShouldContain(".jpg");
        (staged.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeLessThan(TimeSpan.FromMinutes(16));

        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;
        (await harness.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Size_mismatch_is_rejected()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = [0xFF, 0xD8, 0xFF]; // shorter than staged
        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("attachment_size_mismatch");
    }

    [Fact]
    public async Task Spoofed_bytes_are_rejected_as_malicious()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", PdfBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = PdfBytes; // PDF bytes declared as JPEG
        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("malicious_attachment");
    }

    [Fact]
    public async Task Infected_bytes_are_rejected_like_eicar()
    {
        var harness = Create(scanner: new FakeScanner(new(AttachmentScanOutcome.Infected, "Eicar-Test-Signature")));
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;
        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("malicious_attachment");
        failure.Message.ShouldContain("Eicar-Test-Signature");
    }

    [Fact]
    public async Task Missing_upload_is_reported_as_incomplete()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("attachment_upload_incomplete");
    }

    [Fact]
    public async Task Expired_staging_is_rejected_with_gone()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        await harness.Expire(staged.Id);
        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("attachment_expired");
        failure.StatusCode.ShouldBe(410);
    }

    [Fact]
    public async Task Cross_tenant_complete_and_download_are_denied()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Create(dbName, TenantA);
        var tenantB = Create(dbName, TenantB);
        var staged = await tenantA.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        tenantA.Storage.Objects[staged.ObjectKey] = JpegBytes;

        (await tenantB.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeFalse();
        (await tenantB.Service.DownloadAsync(staged.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Download_returns_a_short_lived_presigned_url_after_claim()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("notes.pdf", "application/pdf", PdfBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = PdfBytes;
        (await harness.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeTrue();

        var download = await harness.Service.DownloadAsync(staged.Id, CancellationToken.None);
        download.ShouldNotBeNull();
        download!.DownloadUrl.ShouldStartWith("https://storage.test/");
        download.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task Cleanup_expires_stale_records_and_deletes_bytes()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;
        await harness.Expire(staged.Id, alreadyPast: true);

        (await harness.Service.CleanupExpiredAsync(CancellationToken.None)).ShouldBe(1);
        harness.Storage.Objects.ShouldNotContainKey(staged.ObjectKey);
        (await harness.Service.DownloadAsync(staged.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Completing_moves_an_attachment_to_ready_with_metadata()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;

        (await harness.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeTrue();
        var item = await harness.Get(staged.Id);
        item.Status.ShouldBe(AttachmentStatus.Ready);
        item.CompletedAt.ShouldNotBeNull();
        item.DetectedContentType.ShouldBe("image/jpeg");
        item.MessageId.ShouldBeNull();

        var download = await harness.Service.DownloadAsync(staged.Id, CancellationToken.None);
        download.ShouldNotBeNull();
    }

    [Fact]
    public async Task Completing_an_already_completed_attachment_is_rejected()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;
        (await harness.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeTrue();

        var failure = await Should.ThrowAsync<InboxException>(harness.Service.CompleteAsync(staged.Id, CancellationToken.None));
        failure.Code.ShouldBe("attachment_already_claimed");
    }

    [Fact]
    public async Task Download_of_uncompleted_staging_bytes_is_denied()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes; // bytes uploaded but never completed/scanned

        (await harness.Service.DownloadAsync(staged.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Cleanup_expires_ready_but_unclaimed_objects()
    {
        var harness = Create();
        var staged = await harness.Service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, CancellationToken.None);
        harness.Storage.Objects[staged.ObjectKey] = JpegBytes;
        (await harness.Service.CompleteAsync(staged.Id, CancellationToken.None)).ShouldBeTrue();
        await harness.Expire(staged.Id, alreadyPast: true);

        (await harness.Service.CleanupExpiredAsync(CancellationToken.None)).ShouldBe(1);
        var item = await harness.Get(staged.Id);
        item.Status.ShouldBe(AttachmentStatus.Expired);
        harness.Storage.Objects.ShouldNotContainKey(staged.ObjectKey);
    }

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static Harness Create(string? dbName = null, Guid? tenantId = null, IAttachmentScanner? scanner = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        tenantId ??= TenantA;
        var tenant = new TestTenant(tenantId.Value);
        var options = new DbContextOptionsBuilder<InboxDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new InboxDbContext(options, tenant);
        var storage = new FakeStorage();
        var service = new AttachmentService(db, tenant, storage, scanner ?? new FakeScanner(new(AttachmentScanOutcome.Clean, null)), new TestEnvironment());
        return new(service, storage, db);
    }

    private sealed record Harness(AttachmentService Service, FakeStorage Storage, InboxDbContext Db)
    {
        public async Task Expire(Guid id, bool alreadyPast = false)
        {
            var item = await Db.Attachments.SingleAsync(x => x.Id == id);
            item.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(alreadyPast ? -20 : -1);
            await Db.SaveChangesAsync();
        }

        public Task<Attachment> Get(Guid id) => Db.Attachments.SingleAsync(x => x.Id == id);
    }

    private sealed class TestTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid? TenantId => tenantId;
        public Guid? UserId => Guid.NewGuid();
        public UserRole? Role => UserRole.Owner;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => Task.FromResult($"https://storage.test/{objectKey}?put=1");
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => Task.FromResult($"https://storage.test/{objectKey}?get=1");
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(Objects[objectKey], writable: false));
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<StoredObjectInfo?>(Objects.TryGetValue(objectKey, out var bytes) ? new(bytes.Length, null) : null);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Objects.Remove(objectKey); return Task.CompletedTask; }
    }

    private sealed class FakeScanner(AttachmentScanResult result) : IAttachmentScanner
    {
        public bool IsConfigured => true;
        public Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
