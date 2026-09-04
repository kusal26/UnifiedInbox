using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public enum InboundMediaResult { Stored, Skipped }

/// <summary>
/// Authenticated, private ingestion of inbound WhatsApp media. Bytes are downloaded from the
/// Graph URL (never exposed to the client), capped at 10 MB, magic-byte verified, malware scanned,
/// and stored under a deterministic tenant-scoped object key. The <c>Claimed</c> attachment row is
/// only created after the object and database work would succeed; duplicate webhook deliveries and
/// retries collapse onto one attachment via (TenantId, MessageId, ProviderMediaId) uniqueness.
/// </summary>
public sealed class InboundMediaIngestor(
    InboxDbContext db,
    IObjectStorage storage,
    IAttachmentScanner scanner,
    IHostEnvironment environment,
    IWhatsAppGraphClient graph,
    IConfiguration configuration,
    IHttpClientFactory httpFactory)
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "video/mp4", "application/pdf",
    };

    public async Task<InboundMediaResult> IngestAsync(Channel channel, Message inbound, WhatsAppInbound input, CancellationToken token)
    {
        if (input.MediaId is null || !TryResolveType(input, out var mediaType)) return InboundMediaResult.Skipped;
        var accessToken = await ResolveAccessTokenAsync(channel, token);
        GraphMediaMetadata metadata;
        try { metadata = await graph.GetMediaAsync(input.MediaId, accessToken, token); }
        catch (InboxException exception) when (exception.Code == "provider_unauthorized")
        {
            throw new InboxException("channel_authorization_expired", "The channel credential was rejected by WhatsApp. Reauthorize to continue.", 502);
        }
        if (metadata.FileSize is > MaximumBytes) return InboundMediaResult.Skipped;

        using var client = httpFactory.CreateClient("whatsapp.media");
        using var request = new HttpRequestMessage(HttpMethod.Get, metadata.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        byte[] bytes;
        using (var output = new MemoryStream())
        {
            await using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[16384];
            int read;
            long total = 0;
            while ((read = await body.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > MaximumBytes) return InboundMediaResult.Skipped;
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (total == 0 || (metadata.FileSize is { } expected && expected != total)) return InboundMediaResult.Skipped;
            bytes = output.ToArray();
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (!AttachmentSniffer.Matches(mediaType, head)) return InboundMediaResult.Skipped; // spoofed bytes
        var scan = await ScanAsync(bytes, token);
        if (scan == AttachmentScanOutcome.Infected) return InboundMediaResult.Skipped;

        var objectKey = ObjectKey(channel, inbound, input, mediaType);
        await storage.StoreAsync(objectKey, mediaType, new MemoryStream(bytes, writable: false), token);
        db.Attachments.Add(new Attachment
        {
            TenantId = channel.TenantId,
            MessageId = inbound.Id,
            UploaderId = null,
            ProviderMediaId = input.MediaId,
            ObjectKey = objectKey,
            FileName = SafeName(input, mediaType),
            ContentType = mediaType,
            DetectedContentType = mediaType,
            Size = bytes.Length,
            Status = AttachmentStatus.Claimed,
            CompletedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        });
        return InboundMediaResult.Stored;
    }

    private async Task<string> ResolveAccessTokenAsync(Channel channel, CancellationToken token)
    {
        var credential = await db.ChannelCredentials.SingleOrDefaultAsync(x => x.ChannelId == channel.Id, token)
            ?? throw new InboxException("channel_authorization_expired", "The channel has no stored credential. Reauthorize to continue.", 502);
        var active = Convert.FromBase64String(configuration["Credentials:MasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_MASTER_KEY") ?? throw new InvalidOperationException("Credentials:MasterKey is required."));
        var previousRaw = configuration["Credentials:PreviousMasterKey"] ?? Environment.GetEnvironmentVariable("CREDENTIAL_PREVIOUS_MASTER_KEY");
        var protector = new CredentialProtector(active, string.IsNullOrWhiteSpace(previousRaw) ? null : Convert.FromBase64String(previousRaw));
        try { return protector.Unprotect(credential.EncryptedAccessToken); }
        catch (System.Security.Cryptography.CryptographicException) { throw new InboxException("channel_authorization_expired", "The stored credential cannot be decrypted. Reauthorize to continue.", 502); }
    }

    private async Task<AttachmentScanOutcome> ScanAsync(byte[] bytes, CancellationToken token)
    {
        if (!scanner.IsConfigured)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Test")) return AttachmentScanOutcome.Clean;
            throw new InboxException("attachment_scan_unavailable", "Attachment scanning is unavailable.", 503);
        }
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            return (await scanner.ScanAsync(content, token)).Outcome;
        }
        catch (Exception)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Test")) return AttachmentScanOutcome.Clean;
            throw new InboxException("attachment_scan_unavailable", "Attachment scanning is unavailable.", 503);
        }
    }

    private static bool TryResolveType(WhatsAppInbound input, out string mediaType)
    {
        mediaType = "";
        if (input.Kind is not (WhatsAppInboundKind.Image or WhatsAppInboundKind.Video or WhatsAppInboundKind.Document)) return false;
        var declared = input.DeclaredMimeType?.Trim().ToLowerInvariant() ?? "";
        if (!Allowed.Contains(declared)) return false;
        mediaType = declared;
        return true;
    }

    private static string ObjectKey(Channel channel, Message inbound, WhatsAppInbound input, string mediaType) =>
        $"inbound/{channel.TenantId:N}/{channel.Id:N}/{Safe(inbound.ExternalMessageId)}/{Safe(input.MediaId!)}{ExtensionFor(mediaType)}";

    private static string SafeName(WhatsAppInbound input, string mediaType)
    {
        var name = input.FileName?.Replace('\\', '/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(name) ? $"media-{Safe(input.MediaId!)}{ExtensionFor(mediaType)}" : name;
    }

    private static string Safe(string? value) => string.Concat((value ?? "").Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-'));

    private static string ExtensionFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        _ => ".pdf",
    };
}
