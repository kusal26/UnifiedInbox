using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application;

namespace UnifiedInbox.Infrastructure.Storage;

/// <summary>ClamAV TCP client using the INSTREAM protocol. Fail-closed: callers reject uploads when unconfigured outside Development/Test.</summary>
public sealed class ClamAvScanner : IAttachmentScanner
{
    private readonly string? host;
    private readonly int port;

    public ClamAvScanner(IConfiguration configuration)
    {
        host = configuration["ClamAv:Host"] ?? Environment.GetEnvironmentVariable("CLAMAV_HOST");
        port = int.TryParse(configuration["ClamAv:Port"] ?? Environment.GetEnvironmentVariable("CLAMAV_PORT"), out var parsed) ? parsed : 3310;
    }

    /// <summary>Test/diagnostics hook for pointing the scanner at a stub server.</summary>
    public ClamAvScanner(string? host, int port = 3310)
    {
        this.host = host;
        this.port = port;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(host);

    public async Task<AttachmentScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("ClamAV is not configured.");
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        int read;
        if (content.CanSeek) content.Position = 0;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var length = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(read));
            await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await stream.WriteAsync(new byte[4], cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
        {
            var threat = response.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
            return new(AttachmentScanOutcome.Infected, threat);
        }
        return new(AttachmentScanOutcome.Clean, null);
    }
}
