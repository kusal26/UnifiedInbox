using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Inbound media is downloaded privately with the channel token, capped at 10 MB, magic-byte
/// verified, malware scanned, and stored under a deterministic tenant-scoped key. Only after the
/// object and database work would succeed is a <c>Claimed</c> attachment created against the
/// inbound message; unsupported, spoofed, oversized, and infected media are never stored, and
/// retries collapse onto a single attachment.
/// </summary>
public sealed class InboundMediaIngestionTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
    private static readonly string MasterKey = Convert.ToBase64String(SHA256.HashData("inbound-master"u8.ToArray()));

    [Fact]
    public async Task Supported_image_is_downloaded_scanned_and_claimed_onto_the_message()
    {
        var seed = await SeedAsync();
        var clientFactory = ClientFactory(Jpeg);
        var graph = new FakeMediaGraph(Jpeg.Length);
        await NormalizeAsync(seed, Webhook("wamid.in", """{"id":"media-1","mime_type":"image/jpeg","caption":"look"}"""), "image", clientFactory, graph);

        await using var check = Context(seed);
        var message = await check.Messages.SingleAsync(x => x.ExternalMessageId == "wamid.in");
        message.Body.ShouldBe("look");
        var attachment = await check.Attachments.SingleAsync();
        attachment.MessageId.ShouldBe(message.Id);
        attachment.ProviderMediaId.ShouldBe("media-1");
        attachment.Status.ShouldBe(AttachmentStatus.Claimed);
        attachment.UploaderId.ShouldBeNull();
        attachment.ObjectKey.ShouldStartWith($"inbound/{seed.TenantId:N}/");
        seed.Storage.Objects.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Duplicate_webhook_delivery_creates_one_message_and_one_attachment()
    {
        var seed = await SeedAsync();
        var clientFactory = ClientFactory(Jpeg);
        var graph = new FakeMediaGraph(Jpeg.Length);
        var receiptId = await AddReceiptAsync(seed, Webhook("wamid.dup", """{"id":"media-1","mime_type":"image/jpeg"}"""));

        var first = await NormalizeReceiptAsync(seed, receiptId, clientFactory, graph);
        var second = await NormalizeReceiptAsync(seed, receiptId, clientFactory, graph);

        first.ShouldBe(WebhookOutcome.Processed);
        second.ShouldBe(WebhookOutcome.Ignored);
        await using var check = Context(seed);
        (await check.Messages.IgnoreQueryFilters().CountAsync(x => x.ExternalMessageId == "wamid.dup")).ShouldBe(1);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        seed.Storage.Objects.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Transient_download_failure_retries_and_stores_exactly_once()
    {
        var seed = await SeedAsync();
        var failing = ClientFactory(new HttpRequestException("graph down"));
        var graph = new FakeMediaGraph(Jpeg.Length);
        var receiptId = await AddReceiptAsync(seed, Webhook("wamid.retry", """{"id":"media-1","mime_type":"image/jpeg"}"""));

        (await NormalizeReceiptAsync(seed, receiptId, failing, graph)).ShouldBe(WebhookOutcome.RetryScheduled);
        (await NormalizeReceiptAsync(seed, receiptId, ClientFactory(Jpeg), graph)).ShouldBe(WebhookOutcome.Processed);

        await using var check = Context(seed);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        (await check.Messages.IgnoreQueryFilters().CountAsync(x => x.ExternalMessageId == "wamid.retry")).ShouldBe(1);
        seed.Storage.Objects.ShouldHaveSingleItem();
        seed.Storage.Objects.Single().Value.ShouldBe(Jpeg);
    }

    [Fact]
    public async Task Infected_media_is_never_stored_but_the_message_remains_visible()
    {
        var seed = await SeedAsync(scanner: new FakeScanner(AttachmentScanOutcome.Infected));
        var clientFactory = ClientFactory(Eicar);
        var graph = new FakeMediaGraph(Eicar.Length);

        (await NormalizeAsync(seed, Webhook("wamid.eicar", """{"id":"media-1","mime_type":"image/jpeg"}"""), "image", clientFactory, graph)).ShouldBe(WebhookOutcome.Processed);

        await using var check = Context(seed);
        (await check.Messages.IgnoreQueryFilters().CountAsync(x => x.ExternalMessageId == "wamid.eicar")).ShouldBe(1);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        seed.Storage.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Spoofed_bytes_are_skipped_without_storage()
    {
        var seed = await SeedAsync();
        var spoofBytes = Encoding.ASCII.GetBytes("%PDF-1.4 fake");
        var clientFactory = ClientFactory(spoofBytes);
        var graph = new FakeMediaGraph(spoofBytes.Length);

        // Declared as image/jpeg but the bytes the download URL serves are a PDF.
        var download = Webhook("wamid.spoof", """{"id":"media-1","mime_type":"image/jpeg"}""", "image");
        (await NormalizeAsync(seed, download, "image", clientFactory, graph)).ShouldBe(WebhookOutcome.Processed);

        await using var check = Context(seed);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        seed.Storage.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Oversized_media_is_skipped_without_storage()
    {
        var seed = await SeedAsync();
        var clientFactory = ClientFactory(new byte[1024]);
        var graph = new FakeMediaGraph(InboundMediaIngestor.MaximumBytes + 1);

        (await NormalizeAsync(seed, Webhook("wamid.big", """{"id":"media-1","mime_type":"video/mp4"}"""), "video", clientFactory, graph)).ShouldBe(WebhookOutcome.Processed);

        await using var check = Context(seed);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        seed.Storage.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Audio_and_unsupported_documents_are_not_ingested_but_stay_visible()
    {
        var seed = await SeedAsync();
        var clientFactory = ClientFactory(Jpeg);
        var graph = new FakeMediaGraph(Jpeg.Length);
        var audioWebhook = Webhook("wamid.audio", """{"id":"media-a","mime_type":"audio/ogg"}""", "audio");
        var audioParsed = new WhatsAppPayloadParser().ParseFull(System.Text.Json.JsonDocument.Parse(audioWebhook).RootElement).Messages;
        audioParsed.ShouldHaveSingleItem().Kind.ShouldBe(WhatsAppInboundKind.Audio);

        (await NormalizeAsync(seed, audioWebhook, "audio", clientFactory, graph)).ShouldBe(WebhookOutcome.Processed);
        (await NormalizeAsync(seed, Webhook("wamid.doc", """{"id":"media-d","mime_type":"text/plain","filename":"notes.txt"}"""), "document", clientFactory, graph)).ShouldBe(WebhookOutcome.Processed);

        await using var check = Context(seed);
        (await check.Attachments.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await check.Messages.IgnoreQueryFilters().CountAsync(x => x.ExternalMessageId == "wamid.audio")).ShouldBe(1);
        (await check.Messages.IgnoreQueryFilters().SingleAsync(x => x.ExternalMessageId == "wamid.audio")).Body.ShouldBe("[audio message]");
        seed.Storage.Objects.ShouldBeEmpty();
    }

    private static async Task<WebhookOutcome> NormalizeAsync(Seed seed, string body, string field, IHttpClientFactory http, FakeMediaGraph graph)
    {
        var receipt = await AddReceiptAsync(seed, body);
        return await NormalizeReceiptAsync(seed, receipt, http, graph);
    }

    private static async Task<WebhookOutcome> NormalizeReceiptAsync(Seed seed, Guid receiptId, IHttpClientFactory http, FakeMediaGraph graph)
    {
        await using var db = Context(seed);
        var ingestor = Ingestor(db, seed, graph, http, seed.Scanner);
        var processor = new MessageProcessor(db, Sender(), NullLogger<MessageProcessor>.Instance, ingestor);
        return await processor.NormalizeWebhookAsync(receiptId, CancellationToken.None);
    }

    private static async Task<Seed> SeedAsync(IAttachmentScanner? scanner = null)
    {
        var tenantId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var storage = new MemoryObjectStorage();
        await using (var db = Context(tenantId, channelId))
        {
            db.Tenants.Add(new Tenant(tenantId, "inbound-" + tenantId.ToString("N")[..6], "Inbound"));
            var user = Guid.NewGuid();
            db.Users.Add(new User(user, tenantId, "owner@example.com", "Owner", UserRole.Owner) { NormalizedEmail = "OWNER@EXAMPLE.COM", EmailVerifiedAt = DateTimeOffset.UtcNow });
            db.Channels.Add(new Channel(channelId, tenantId, "whatsapp", "phone-in", true) { ExternalBusinessId = "waba-in" });
            db.ChannelCredentials.Add(new ChannelCredential { TenantId = tenantId, ChannelId = channelId, EncryptedAccessToken = new CredentialProtector(Convert.FromBase64String(MasterKey)).Protect("channel-token") });
            await db.SaveChangesAsync();
        }
        return new Seed(tenantId, channelId, storage, scanner ?? new FakeScanner(AttachmentScanOutcome.Clean));
    }

    private static async Task<Guid> AddReceiptAsync(Seed seed, string body)
    {
        await using var db = Context(seed);
        var receipt = new global::UnifiedInbox.Domain.WebhookReceipt { TenantId = seed.TenantId, ChannelId = seed.ChannelId, ProviderEventId = Guid.NewGuid().ToString("N"), RawBody = Encoding.UTF8.GetBytes(body) };
        db.WebhookReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private static string Webhook(string externalMessageId, string mediaJson, string field = "image") =>
        "{\"entry\":[{\"changes\":[{\"value\":{\"metadata\":{\"phone_number_id\":\"phone-in\"},\"messages\":[{\"from\":\"15550001\",\"id\":\"" + externalMessageId + "\",\"" + field + "\":" + mediaJson + "}]}}]}]}";

    private static InboundMediaIngestor Ingestor(InboxDbContext db, Seed seed, IWhatsAppGraphClient graph, IHttpClientFactory http, IAttachmentScanner scanner)
    {
        var configuration = new DictionaryConfiguration(new Dictionary<string, string?> { ["Credentials:MasterKey"] = MasterKey });
        return new(db, seed.Storage, scanner, new TestEnvironment(), graph, configuration, http);
    }

    private static WhatsAppMessageSender Sender() => new(new HttpClient(), new DictionaryConfiguration([]), new TestEnvironment());

    private static InboxDbContext Context(Seed seed) => Context(seed.TenantId, seed.ChannelId);
    private static InboxDbContext Context(Guid tenantId, Guid channelId)
    {
        var name = "inbound-" + tenantId.ToString("N");
        var tenant = new TestTenant(tenantId, channelId);
        return new(new DbContextOptionsBuilder<InboxDbContext>().UseInMemoryDatabase(name).Options, tenant);
    }

    private static IHttpClientFactory ClientFactory(byte[] bytes) => new StubClientFactory(new ByteHandler(bytes));
    private static IHttpClientFactory ClientFactory(Exception failure) => new StubClientFactory(new FailingHandler(failure));

    private sealed record Seed(Guid TenantId, Guid ChannelId, MemoryObjectStorage Storage, IAttachmentScanner Scanner)
    {
        public byte[] Bytes { get; set; } = [];
    }

    private sealed record TestTenant(Guid TenantId, Guid ChannelId) : ICurrentTenant
    {
        Guid? ICurrentTenant.TenantId => TenantId;
        public Guid? UserId => null;
        public UserRole? Role => null;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeMediaGraph(long size) : IWhatsAppGraphClient
    {
        public Task<string> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GraphPhoneNumber> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetBusinessNameAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetTokenScopesAsync(string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UnsubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WhatsAppTemplateInfo>> ListMessageTemplatesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WhatsAppTemplateInfo>>([]);
        public Task<GraphMediaMetadata> GetMediaAsync(string mediaId, string accessToken, CancellationToken cancellationToken) => Task.FromResult(new GraphMediaMetadata("https://graph.download/" + mediaId, "image/jpeg", size));
    }

    private sealed class FakeScanner(AttachmentScanOutcome outcome) : IAttachmentScanner
    {
        public bool IsConfigured => true;
        public Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => Task.FromResult(new AttachmentScanResult(outcome, outcome == AttachmentScanOutcome.Infected ? "Eicar-Test-Signature" : null));
    }

    private sealed class MemoryObjectStorage : IObjectStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public Task StoreAsync(string objectKey, string contentType, Stream content, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            Objects[objectKey] = buffer.ToArray();
            return Task.CompletedTask;
        }
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(Objects[objectKey], writable: false));
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<StoredObjectInfo?>(Objects.TryGetValue(objectKey, out var bytes) ? new StoredObjectInfo(bytes.Length, null) : null);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) { Objects.Remove(objectKey); return Task.CompletedTask; }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class ByteHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw failure;
    }
}
