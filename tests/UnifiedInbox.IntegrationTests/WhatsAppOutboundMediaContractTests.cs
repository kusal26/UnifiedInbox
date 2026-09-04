using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Outbound media contract: a claimed attachment's clean bytes are uploaded through the Graph
/// /media endpoint once, the returned provider media id is kept on the delivery part, and the part
/// is then sent by media id (image/video/document). The media id is never exposed on attachment DTOs.
/// </summary>
public sealed class WhatsAppOutboundMediaContractTests
{
    private static readonly string MasterKey = Convert.ToBase64String(SHA256.HashData("media-master"u8.ToArray()));
    private static readonly Guid TenantId = Guid.NewGuid();

    [Theory]
    [InlineData(DeliveryPartKind.Image, "image/jpeg", "photo.jpg", "image")]
    [InlineData(DeliveryPartKind.Video, "video/mp4", "clip.mp4", "video")]
    [InlineData(DeliveryPartKind.Document, "application/pdf", "brief.pdf", "document")]
    public async Task Claimed_media_is_uploaded_then_sent_by_media_id(DeliveryPartKind kind, string mediaType, string fileName, string graphType)
    {
        var (harness, part) = await SeedAsync(kind, mediaType, fileName);
        using var handler = new MediaHandler();

        (await NewSender(harness.Db, handler).SendPartAsync(harness.Db, harness.Channel, harness.Contact, "", part, CancellationToken.None)).ShouldBe("wamid.media");

        handler.Requests.Count.ShouldBe(2);
        var upload = handler.Requests[0];
        upload.RequestUri!.AbsolutePath.ShouldEndWith("/phone-1/media");
        var uploadBody = handler.Bodies[0];
        uploadBody.ShouldContain(fileName);
        uploadBody.ShouldContain(mediaType);
        part.ProviderMediaId.ShouldBe("media-123");

        var send = handler.Requests[1];
        send.RequestUri!.AbsolutePath.ShouldEndWith("/phone-1/messages");
        using var body = JsonDocument.Parse(handler.Bodies[1]);
        body.RootElement.GetProperty("type").GetString().ShouldBe(graphType);
        body.RootElement.GetProperty(graphType).GetProperty("id").GetString().ShouldBe("media-123");
    }

    [Fact]
    public async Task A_persisted_provider_media_id_is_reused_without_a_reupload()
    {
        var (harness, part) = await SeedAsync(DeliveryPartKind.Image, "image/jpeg", "photo.jpg");
        part.ProviderMediaId = "media-kept";
        using var handler = new MediaHandler();

        (await NewSender(harness.Db, handler).SendPartAsync(harness.Db, harness.Channel, harness.Contact, "", part, CancellationToken.None)).ShouldBe("wamid.media");

        handler.Requests.ShouldHaveSingleItem(); // only the message send; no /media call
        part.ProviderMediaId.ShouldBe("media-kept");
    }

    [Fact]
    public async Task An_attachment_not_claimed_to_the_message_cannot_be_sent()
    {
        var (harness, part) = await SeedAsync(DeliveryPartKind.Image, "image/jpeg", "photo.jpg");
        using var handler = new MediaHandler();
        var other = new MessageDeliveryPart { TenantId = TenantId, MessageId = Guid.NewGuid(), Kind = DeliveryPartKind.Image, AttachmentId = part.AttachmentId };

        var failure = await Should.ThrowAsync<InboxException>(() => NewSender(harness.Db, handler).SendPartAsync(harness.Db, harness.Channel, harness.Contact, "", other, CancellationToken.None));
        failure.Code.ShouldBe("attachment_not_claimed");
        handler.Requests.ShouldBeEmpty();
    }

    private static async Task<(Harness Db, MessageDeliveryPart Part)> SeedAsync(DeliveryPartKind kind, string mediaType, string fileName)
    {
        var (db, _) = TestContexts.Create(TenantId, Guid.NewGuid());
        var channel = new Channel(Guid.NewGuid(), TenantId, "whatsapp", "phone-1", true) { DisplayName = "Sales" };
        db.Channels.Add(channel);
        db.ChannelCredentials.Add(new ChannelCredential { TenantId = TenantId, ChannelId = channel.Id, EncryptedAccessToken = new CredentialProtector(Convert.FromBase64String(MasterKey)).Protect("provider-token") });
        var contact = new Contact(Guid.NewGuid(), TenantId, "whatsapp", "phone-1", "15550001", "C", "+15550001");
        db.Contacts.Add(contact);
        var messageId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            TenantId = TenantId,
            UploaderId = Guid.NewGuid(),
            MessageId = messageId,
            ObjectKey = $"obj/{TenantId:N}/{attachmentId:N}/{fileName}",
            FileName = fileName,
            ContentType = mediaType,
            DetectedContentType = mediaType,
            Size = 8,
            Status = AttachmentStatus.Claimed,
            CompletedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        db.SaveChanges();
        var part = new MessageDeliveryPart { TenantId = TenantId, MessageId = messageId, Position = 0, Kind = kind, AttachmentId = attachmentId };
        return (new Harness(db, channel, contact, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), part);
    }

    private static WhatsAppMessageSender NewSender(InboxDbContext db, MediaHandler handler)
    {
        var configuration = new DictionaryConfiguration(new Dictionary<string, string?> { ["Credentials:MasterKey"] = MasterKey });
        return new WhatsAppMessageSender(new HttpClient(handler), configuration, new ProductionEnvironment(), new MemoryStorage());
    }

    private sealed record Harness(InboxDbContext Db, Channel Channel, Contact Contact, byte[] Bytes) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private sealed class MediaHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var isUpload = request.RequestUri!.AbsolutePath.EndsWith("/media", StringComparison.Ordinal);
            var json = isUpload ? """{"id":"media-123"}""" : """{"messages":[{"id":"wamid.media"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class MemoryStorage : IObjectStorage
    {
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes("filebytes")));
        public Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken) => Task.FromResult("https://storage.test/" + objectKey);
        public Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken) => Task.FromResult("https://storage.test/" + objectKey);
        public Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken) => Task.FromResult<StoredObjectInfo?>(new StoredObjectInfo(9, null));
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
