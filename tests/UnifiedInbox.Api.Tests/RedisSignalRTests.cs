using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using Shouldly;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Messaging;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Tests;

/// <summary>
/// Proves canonical realtime fan-out across two real API instances sharing one Redis + RabbitMQ:
/// a tenant client connected to each instance receives a published event exactly once, a second
/// tenant sees nothing, and a reconnecting client resumes delivery. Every API instance runs the
/// real Program (RealtimeSubscriber over the broker, Redis SignalR backplane) as <c>app_runtime</c>.
/// </summary>
public sealed class RedisSignalRTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:8-alpine").WithPortBinding(6379, true).Build();

    private string ownerConnection = "";
    private string runtimeConnection = "";
    private string redisConnection = "";
    private string rabbitConnection = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync(), redis.StartAsync());
        await WaitForPortAsync(redis.GetMappedPublicPort(6379));
        await using var admin = new NpgsqlConnection(postgres.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using (var owner = Context(postgres.GetConnectionString())) await owner.Database.MigrateAsync();
        ownerConnection = postgres.GetConnectionString();
        runtimeConnection = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
        redisConnection = $"127.0.0.1:{redis.GetMappedPublicPort(6379)}";
        rabbitConnection = rabbit.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
    }

    [DockerFact]
    public async Task A_canonical_event_reaches_one_client_per_api_instance_and_no_other_tenant()
    {
        const string password = "supersecure-password-1";
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedWorkspaceAsync(tenantA, "fanout-a", password);
        await SeedWorkspaceAsync(tenantB, "fanout-b", password);

        using var hostA = ApiHost();
        using var hostB = ApiHost();
        _ = hostA.Server; _ = hostB.Server; // start both servers

        var tokenA = await LoginAsync(hostA, "fanout-a", password);
        var tokenB = await LoginAsync(hostA, "fanout-b", password);

        await using var clientA = await ConnectAsync(hostA, tokenA);
        await using var clientB = await ConnectAsync(hostB, tokenA);
        await using var clientC = await ConnectAsync(hostA, tokenB);
        await Task.WhenAll(clientA.UntilConnectedAsync(), clientB.UntilConnectedAsync(), clientC.UntilConnectedAsync());
        await Task.Delay(1500); // let the realtime subscriber + Redis group membership settle

        await PublishCanonicalAsync(tenantA, "message.created", """{"id":"00000000-0000-0000-0000-00000000aaaa"}""");
        await Task.WhenAll(clientA.WaitForAsync(1), clientB.WaitForAsync(1));
        await Task.Delay(1000);
        clientA.Count.ShouldBe(1);
        clientB.Count.ShouldBe(1);
        clientC.Count.ShouldBe(0); // the other tenant must not receive the event

        // Reconnect and confirm delivery resumes for the re-joined tenant.
        await clientA.Connection.DisposeAsync();
        await using var reconnected = await ConnectAsync(hostB, tokenA);
        await reconnected.UntilConnectedAsync();
        await Task.Delay(1000);
        await PublishCanonicalAsync(tenantA, "conversation.updated", """{"id":"00000000-0000-0000-0000-00000000bbbb"}""");
        await Task.WhenAll(reconnected.WaitForAsync(1), clientB.WaitForAsync(2));
        reconnected.Count.ShouldBe(1);
        clientB.Count.ShouldBe(2);
        clientC.Count.ShouldBe(0);
    }

    private async Task SeedWorkspaceAsync(Guid tenantId, string slug, string password)
    {
        await using var db = Context(ownerConnection);
        db.Tenants.Add(new Tenant(tenantId, slug, "Fanout " + slug[^1]));
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
        var user = new User(Guid.NewGuid(), tenantId, $"{slug}@example.com", "Owner", UserRole.Owner) { NormalizedEmail = $"{slug.ToUpperInvariant()}@EXAMPLE.COM", EmailVerifiedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private WebApplicationFactory<Program> ApiHost()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedInbox.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root was not found.");
        var contentRoot = Path.Combine(directory.FullName, "src", "backend", "UnifiedInbox.Api");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("ConnectionStrings:Database", runtimeConnection);
            builder.UseSetting("RabbitMq:Connection", rabbitConnection);
            builder.UseSetting("Redis:Connection", redisConnection);
            builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-that-is-long-enough-1234567890");
            builder.UseSetting("WhatsApp:AppSecret", "unit-test-webhook-app-secret");
            builder.UseSetting("WhatsApp:VerifyToken", "unit-test-verify-token");
            builder.UseSetting("WhatsApp:UseFake", "true");
        });
    }

    private static async Task<string> LoginAsync(WebApplicationFactory<Program> host, string slug, string password)
    {
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email = $"{slug}@example.com", password });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.AccessToken!;
    }

    private static async Task<CountingHub> ConnectAsync(WebApplicationFactory<Program> host, string token)
    {
        var hub = new CountingHub();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, "/hubs/inbox"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.On<object>("message.created", _ => hub.Bump());
        connection.On<object>("conversation.updated", _ => hub.Bump());
        connection.Closed += exception => { hub.CloseReason = exception; return Task.CompletedTask; };
        await connection.StartAsync();
        hub.Connection = connection;
        return hub;
    }

    private async Task PublishCanonicalAsync(Guid tenantId, string type, string payload)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbitConnection), AutomaticRecoveryEnabled = true }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(channel);
        var properties = new BasicProperties
        {
            Persistent = true,
            Type = type,
            MessageId = $"{type}:{Guid.NewGuid():N}",
            Headers = new Dictionary<string, object?> { ["tenant-id"] = tenantId.ToString() },
        };
        await channel.BasicPublishAsync(RabbitMqTopology.EventsExchange, type, mandatory: true, properties, Encoding.UTF8.GetBytes(payload));
    }

    private static async Task WaitForPortAsync(int port)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"Port {port} did not become reachable.");
    }

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);

    private sealed class CountingHub : IAsyncDisposable
    {
        private readonly object gate = new();
        private int count;
        public HubConnection Connection { get; set; } = null!;
        public Exception? CloseReason { get; set; }
        public int Count { get { lock (gate) { return count; } } }

        public void Bump() { lock (gate) { count++; } }

        public async Task UntilConnectedAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (Connection.State != HubConnectionState.Connected && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            if (Connection.State != HubConnectionState.Connected)
                throw new InvalidOperationException($"Hub did not connect (state={Connection.State}, closeReason={CloseReason})");
        }

        public async Task WaitForAsync(int expected)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (Count < expected && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            Count.ShouldBeGreaterThanOrEqualTo(expected);
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed record LoginResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
}
