using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

[CollectionDefinition("attachment-claim")]
public sealed class AttachmentClaimCollection : ICollectionFixture<AttachmentClaimFixture>;

public sealed class AttachmentClaimFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string OwnerConnection => container.GetConnectionString();
    public string RuntimeConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(OwnerConnection);
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using var owner = Context(OwnerConnection);
        await owner.Database.MigrateAsync();
        RuntimeConnection = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
    public InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}

/// <summary>
/// Claims must be atomic and only ever bind scanned (<see cref="AttachmentStatus.Ready"/>)
/// objects: staged, already-claimed, expired, duplicate, and cross-tenant attachments are all
/// rejected, and two concurrent sends for the same attachment yield exactly one winner.
/// </summary>
[Collection("attachment-claim")]
public sealed class AttachmentClaimTests(AttachmentClaimFixture fixture)
{
    [DockerFact]
    public async Task Ready_attachments_are_claimed_onto_the_message()
    {
        var (tenant, user, messages) = await SeedAsync(AttachmentStatus.Ready);
        await RunScopedAsync(tenant, async (db, service, token) =>
        {
            await service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId], token);
            var attachment = await db.Attachments.SingleAsync(x => x.Id == messages.AttachmentId, token);
            attachment.Status.ShouldBe(AttachmentStatus.Claimed);
            attachment.MessageId.ShouldBe(messages.MessageA);
        });
        _ = user;
    }

    [DockerFact]
    public async Task Staged_attachments_cannot_be_claimed()
    {
        var (tenant, _, messages) = await SeedAsync(AttachmentStatus.Staged);
        await RunScopedAsync(tenant, async (db, service, token) =>
        {
            var failure = await Should.ThrowAsync<InboxException>(() => service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId], token));
            failure.Code.ShouldBe("attachment_already_claimed");
        });
    }

    [DockerFact]
    public async Task Reuse_after_claim_is_rejected()
    {
        var (tenant, _, messages) = await SeedAsync(AttachmentStatus.Ready);
        await RunScopedAsync(tenant, async (db, service, token) =>
        {
            await service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId], token);
            var failure = await Should.ThrowAsync<InboxException>(() => service.ClaimForMessageAsync(messages.MessageB, [messages.AttachmentId], token));
            failure.Code.ShouldBe("attachment_already_claimed");
        });
    }

    [DockerFact]
    public async Task Duplicate_attachment_ids_are_rejected()
    {
        var (tenant, _, messages) = await SeedAsync(AttachmentStatus.Ready);
        await RunScopedAsync(tenant, async (db, service, token) =>
        {
            var failure = await Should.ThrowAsync<InboxException>(() => service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId, messages.AttachmentId], token));
            failure.Code.ShouldBe("attachment_already_claimed");
        });
    }

    [DockerFact]
    public async Task Expired_ready_attachments_cannot_be_claimed()
    {
        var (tenant, _, messages) = await SeedAsync(AttachmentStatus.Ready, expired: true);
        await RunScopedAsync(tenant, async (db, service, token) =>
        {
            var failure = await Should.ThrowAsync<InboxException>(() => service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId], token));
            failure.Code.ShouldBe("attachment_expired");
        });
    }

    [DockerFact]
    public async Task Cross_tenant_claims_are_denied()
    {
        var (tenant, _, messages) = await SeedAsync(AttachmentStatus.Ready);
        await using var owner = fixture.Context(fixture.OwnerConnection);
        var otherTenant = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        owner.Tenants.Add(new Tenant(otherTenant, "claim-other", "Other"));
        owner.Users.Add(new User(otherUser, otherTenant, "other@example.com", "Other", UserRole.Owner) { NormalizedEmail = "OTHER@EXAMPLE.COM", PasswordHash = "x" });
        await owner.SaveChangesAsync();

        var db = fixture.Context(fixture.RuntimeConnection);
        var service = NewService(db, new ClaimTenant(otherTenant, otherUser));
        await new TenantExecutionScope(db).RunAsync(otherTenant, async token =>
        {
            var failure = await Should.ThrowAsync<InboxException>(() => service.ClaimForMessageAsync(messages.MessageA, [messages.AttachmentId], token));
            failure.Code.ShouldBe("attachment_not_found");
        }, CancellationToken.None);
        _ = tenant;
    }

    [DockerFact]
    public async Task Concurrent_claims_yield_exactly_one_winner()
    {
        var (tenant, user, messages) = await SeedAsync(AttachmentStatus.Ready);
        var winner = 0;
        var conflicts = 0;

        async Task TryClaim(Guid messageId)
        {
            await using var db = fixture.Context(fixture.RuntimeConnection);
            var service = NewService(db, new ClaimTenant(tenant, user));
            try
            {
                await new TenantExecutionScope(db).RunAsync(tenant, token => service.ClaimForMessageAsync(messageId, [messages.AttachmentId], token), CancellationToken.None);
                Interlocked.Increment(ref winner);
            }
            catch (InboxException failure)
            {
                failure.Code.ShouldBe("attachment_already_claimed");
                Interlocked.Increment(ref conflicts);
            }
        }

        await Task.WhenAll(TryClaim(messages.MessageA), TryClaim(messages.MessageB));
        winner.ShouldBe(1);
        conflicts.ShouldBe(1);

        await using var verify = fixture.Context(fixture.RuntimeConnection);
        var claimed = await new TenantExecutionScope(verify).RunAsync(tenant, token => verify.Attachments.SingleAsync(x => x.Id == messages.AttachmentId, token), CancellationToken.None);
        claimed.Status.ShouldBe(AttachmentStatus.Claimed);
    }

    private async Task<(Guid Tenant, Guid User, Seed Messages)> SeedAsync(AttachmentStatus status, bool expired = false)
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var channel = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var messageA = Guid.NewGuid();
        var messageB = Guid.NewGuid();
        var attachment = Guid.NewGuid();

        await using (var owner = fixture.Context(fixture.OwnerConnection))
        {
            owner.Tenants.Add(new Tenant(tenant, "claim-" + tenant.ToString("N")[..6], "Claim"));
            owner.Users.Add(new User(user, tenant, "owner@example.com", "Owner", UserRole.Owner) { NormalizedEmail = "OWNER@EXAMPLE.COM", PasswordHash = "x" });
            owner.Channels.Add(new Channel(channel, tenant, "whatsapp", "phone-c", true));
            owner.Contacts.Add(new Contact(contact, tenant, "whatsapp", "phone-c", "cust", "C", "+1555"));
            owner.Conversations.Add(new Conversation { Id = conversation, TenantId = tenant, ChannelId = channel, ContactId = contact, ExternalConversationId = "cust" });
            owner.Messages.AddRange(
                new Message { Id = messageA, TenantId = tenant, ChannelId = channel, ConversationId = conversation, Direction = MessageDirection.Outbound, Body = "a", Status = MessageStatus.Pending, Sequence = 1 },
                new Message { Id = messageB, TenantId = tenant, ChannelId = channel, ConversationId = conversation, Direction = MessageDirection.Outbound, Body = "b", Status = MessageStatus.Pending, Sequence = 2 });
            owner.Attachments.Add(new Attachment
            {
                Id = attachment,
                TenantId = tenant,
                UploaderId = user,
                ObjectKey = "obj/" + tenant.ToString("N") + "/file.pdf",
                FileName = "file.pdf",
                ContentType = "application/pdf",
                Size = 1,
                Status = status,
                CompletedAt = status == AttachmentStatus.Ready ? DateTimeOffset.UtcNow : null,
                DetectedContentType = status == AttachmentStatus.Ready ? "application/pdf" : null,
                ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddMinutes(15),
            });
            await owner.SaveChangesAsync();
        }

        return (tenant, user, new Seed(messageA, messageB, attachment));
    }

    private async Task RunScopedAsync(Guid tenant, Func<InboxDbContext, AttachmentService, CancellationToken, Task> body)
    {
        await using var db = fixture.Context(fixture.RuntimeConnection);
        var service = NewService(db, new ClaimTenant(tenant, Guid.NewGuid()));
        await new TenantExecutionScope(db).RunAsync(tenant, token => body(db, service, token), CancellationToken.None);
    }

    private static AttachmentService NewService(InboxDbContext db, ICurrentTenant tenant) =>
        new(db, tenant, new UnusedStorage(), new UnusedScanner(), new TestHostEnvironment());

    private sealed record Seed(Guid MessageA, Guid MessageB, Guid AttachmentId);

    private sealed record ClaimTenant(Guid TenantId, Guid UserId) : ICurrentTenant
    {
        Guid? ICurrentTenant.TenantId => TenantId;
        Guid? ICurrentTenant.UserId => UserId;
        public UserRole? Role => UserRole.Owner;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class UnusedStorage : IObjectStorage
    {
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedScanner : IAttachmentScanner
    {
        public bool IsConfigured => true;
        public Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
