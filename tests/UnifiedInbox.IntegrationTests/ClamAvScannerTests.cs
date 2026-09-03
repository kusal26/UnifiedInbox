using System.Net;
using System.Net.Sockets;
using System.Text;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Storage;

namespace UnifiedInbox.IntegrationTests;

public sealed class ClamAvScannerTests
{
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    [Fact]
    public async Task Clean_stream_reports_clean()
    {
        await using var server = new FakeClamAvServer("stream: OK\n");
        var scanner = new ClamAvScanner("127.0.0.1", server.Port);
        var result = await scanner.ScanAsync(new MemoryStream("hello"u8.ToArray()), CancellationToken.None);
        result.Outcome.ShouldBe(AttachmentScanOutcome.Clean);
        result.ThreatName.ShouldBeNull();
    }

    [Fact]
    public async Task Eicar_stream_reports_infected_with_threat_name()
    {
        await using var server = new FakeClamAvServer("stream: Eicar-Test-Signature FOUND\n");
        var scanner = new ClamAvScanner("127.0.0.1", server.Port);
        var result = await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(Eicar)), CancellationToken.None);
        result.Outcome.ShouldBe(AttachmentScanOutcome.Infected);
        result.ThreatName.ShouldBe("Eicar-Test-Signature");
    }

    [Fact]
    public void Missing_host_is_not_configured() => new ClamAvScanner((string?)null).IsConfigured.ShouldBeFalse();

    private sealed class FakeClamAvServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly string response;
        private readonly CancellationTokenSource lifetime = new();

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public FakeClamAvServer(string response)
        {
            this.response = response;
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
                // Consume the INSTREAM command + length-prefixed chunks until the zero-length terminator.
                var buffer = new byte[8192];
                var pending = new List<byte>();
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, lifetime.Token);
                    if (read == 0) break;
                    pending.AddRange(buffer.AsSpan(0, read).ToArray());
                    if (pending.Count >= 4 && pending[^4] == 0 && pending[^3] == 0 && pending[^2] == 0 && pending[^1] == 0) break;
                }
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
