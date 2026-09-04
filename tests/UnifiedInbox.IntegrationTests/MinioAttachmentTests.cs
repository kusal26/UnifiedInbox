using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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
using UnifiedInbox.Infrastructure.Storage;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// End-to-end object lifecycle against a real MinIO server: bytes are PUT through the
/// presigned URL, the API reads them back, magic bytes and (ClamAV-protocol) malware checks
/// run, and the object becomes Ready for claim/download. A clean object completes; an object
/// containing the EICAR test signature is rejected and its bytes are deleted.
/// </summary>
public sealed class MinioAttachmentTests : IAsyncLifetime
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00];
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer minio = new ContainerBuilder("minio/minio:latest")
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithPortBinding(9000, true)
        .Build();
    private FakeClamAvServer clam = null!;
    private string ownerConnection = "";
    private string runtimeConnection = "";
    private string storageEndpoint = "";
    private int clamPort;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), minio.StartAsync());
        await WaitForMinioAsync(minio.GetMappedPublicPort(9000));
        clam = new FakeClamAvServer();
        clamPort = clam.Port;
        storageEndpoint = $"127.0.0.1:{minio.GetMappedPublicPort(9000)}";

        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using (var owner = Context(postgres.GetConnectionString())) await owner.Database.MigrateAsync();
        ownerConnection = postgres.GetConnectionString();
        runtimeConnection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await clam.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), minio.DisposeAsync().AsTask());
    }

    [DockerFact]
    public async Task Uploaded_bytes_complete_to_ready_then_claim_and_download()
    {
        var seeded = await SeedAsync();
        await using var db = Context(runtimeConnection);
        var service = BuildService(db, seeded.Tenant);
        await new TenantExecutionScope(db).RunAsync(seeded.TenantId, async token =>
        {
            var staged = await service.StageAsync("photo.jpg", "image/jpeg", JpegBytes.Length, token);
            await PutAsync(staged.UploadUrl, JpegBytes);

            (await service.CompleteAsync(staged.Id, token)).ShouldBeTrue();
            var ready = await db.Attachments.SingleAsync(x => x.Id == staged.Id, token);
            ready.Status.ShouldBe(AttachmentStatus.Ready);
            ready.CompletedAt.ShouldNotBeNull();
            ready.DetectedContentType.ShouldBe("image/jpeg");

            var download = await service.DownloadAsync(staged.Id, token);
            download.ShouldNotBeNull();
            var bytes = await GetAsync(download!.DownloadUrl);
            bytes.ShouldBe(JpegBytes);

            await service.ClaimForMessageAsync(seeded.MessageId, [staged.Id], token);
            var claimed = await db.Attachments.AsNoTracking().SingleAsync(x => x.Id == staged.Id, token);
            claimed.Status.ShouldBe(AttachmentStatus.Claimed);
            claimed.MessageId.ShouldBe(seeded.MessageId);
        }, CancellationToken.None);
    }

    [DockerFact]
    public async Task Eicar_object_is_rejected_by_scanning_and_deleted()
    {
        var seeded = await SeedAsync();
        await using var db = Context(runtimeConnection);
        var service = BuildService(db, seeded.Tenant);
        var infected = new byte[JpegBytes.Length + Encoding.ASCII.GetByteCount(Eicar)];
        JpegBytes.CopyTo(infected, 0);
        Encoding.ASCII.GetBytes(Eicar).CopyTo(infected, JpegBytes.Length);

        await new TenantExecutionScope(db).RunAsync(seeded.TenantId, async token =>
        {
            var staged = await service.StageAsync("photo.jpg", "image/jpeg", infected.Length, token);
            await PutAsync(staged.UploadUrl, infected);

            var failure = await Should.ThrowAsync<InboxException>(() => service.CompleteAsync(staged.Id, token));
            failure.Code.ShouldBe("malicious_attachment");

            var rejected = await db.Attachments.SingleAsync(x => x.Id == staged.Id, token);
            rejected.Status.ShouldBe(AttachmentStatus.Rejected);
            var storage = BuildStorage();
            (await storage.StatAsync(staged.ObjectKey, token)).ShouldBeNull();
        }, CancellationToken.None);
    }

    private async Task<(Guid TenantId, ClaimTenant Tenant, Guid MessageId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var user = Guid.NewGuid();
        var channel = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var message = Guid.NewGuid();
        await using (var owner = Context(ownerConnection))
        {
            owner.Tenants.Add(new Tenant(tenantId, "minio-" + tenantId.ToString("N")[..6], "Minio"));
            owner.Users.Add(new User(user, tenantId, "owner@example.com", "Owner", UserRole.Owner) { NormalizedEmail = "OWNER@EXAMPLE.COM", PasswordHash = "x" });
            owner.Channels.Add(new Channel(channel, tenantId, "whatsapp", "phone-m", true));
            owner.Contacts.Add(new Contact(contact, tenantId, "whatsapp", "phone-m", "cust", "C", "+1555"));
            owner.Conversations.Add(new Conversation { Id = conversation, TenantId = tenantId, ChannelId = channel, ContactId = contact, ExternalConversationId = "cust" });
            owner.Messages.Add(new Message { Id = message, TenantId = tenantId, ChannelId = channel, ConversationId = conversation, Direction = MessageDirection.Outbound, Body = "x", Status = MessageStatus.Pending, Sequence = 1 });
            await owner.SaveChangesAsync();
        }
        return (tenantId, new ClaimTenant(tenantId, user), message);
    }

    private AttachmentService BuildService(InboxDbContext db, ClaimTenant tenant) => new(db, tenant, BuildStorage(), BuildScanner(), new TestHostEnvironment());

    private MinioObjectStorage BuildStorage() => new(new DictionaryConfiguration(new Dictionary<string, string?>
    {
        ["Storage:Endpoint"] = storageEndpoint,
        ["Storage:PresignEndpoint"] = storageEndpoint,
        ["Storage:AccessKey"] = AccessKey,
        ["Storage:SecretKey"] = SecretKey,
        ["Storage:Bucket"] = "attachments",
        ["Storage:UseSsl"] = "false",
    }));

    private ClamAvScanner BuildScanner() => new("127.0.0.1", clamPort);

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);

    private static async Task PutAsync(string url, byte[] bytes)
    {
        using var client = new HttpClient();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.PutAsync(url, content);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<byte[]> GetAsync(string url)
    {
        using var client = new HttpClient();
        return await client.GetByteArrayAsync(url);
    }

    private static async Task WaitForMinioAsync(int port)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = await client.GetAsync($"http://127.0.0.1:{port}/minio/health/ready");
                if (response.IsSuccessStatusCode) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(500);
        }
        throw new TimeoutException("MinIO did not become ready.");
    }

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

    /// <summary>In-process ClamAV protocol stub: replies to the INSTREAM handshake based on content.</summary>
    private sealed class FakeClamAvServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource lifetime = new();

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public FakeClamAvServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = AcceptLoop();
        }

        private async Task AcceptLoop()
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
                catch (OperationCanceledException) { return; }
                _ = Handle(client);
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var payload = new List<byte>();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, lifetime.Token);
                    if (read == 0) break;
                    payload.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (payload.Count >= 4 && payload[^4] == 0 && payload[^3] == 0 && payload[^2] == 0 && payload[^1] == 0) break;
                }
                var text = Encoding.ASCII.GetString(payload.ToArray());
                var response = text.Contains("EICAR-STANDARD-ANTIVIRUS", StringComparison.Ordinal) ? "stream: Eicar-Test-Signature FOUND\n" : "stream: OK\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), lifetime.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            lifetime.Dispose();
        }
    }
}
