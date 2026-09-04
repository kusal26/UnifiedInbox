using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using UnifiedInbox.Application;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Tests;

[CollectionDefinition("runtime-role")]
public sealed class RuntimeRoleCollection : ICollectionFixture<RuntimeRoleFixture>;

/// <summary>
/// Boots the real API host (WebApplicationFactory over <c>Program</c>) so middleware, DI,
/// JWT auth, controllers, and EF Core all run as <c>app_runtime</c> against a real Postgres
/// database whose tables are under FORCE ROW LEVEL SECURITY.
/// </summary>
public sealed class RuntimeRoleFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string OwnerConnection => container.GetConnectionString();
    public string RuntimeConnection { get; private set; } = "";
    public string AppSecret { get; } = "unit-test-webhook-app-secret";
    public CapturingMail Mail { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

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

        var contentRoot = RepositoryRoot("src/backend/UnifiedInbox.Api");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(contentRoot);
            builder.UseSetting("ConnectionStrings:Database", RuntimeConnection);
            builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-that-is-long-enough-1234567890");
            builder.UseSetting("WhatsApp:AppSecret", AppSecret);
            builder.UseSetting("WhatsApp:VerifyToken", "unit-test-verify-token");
            builder.UseSetting("WhatsApp:UseFake", "true");
            builder.UseSetting("Redis:Connection", "");
            builder.UseSetting("RabbitMq:Connection", "");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMailSender>();
                services.AddSingleton<IMailSender>(Mail);
            });
        });
    }

    public InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await container.DisposeAsync();
    }

    private static string RepositoryRoot(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedInbox.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root was not found.");
        return Path.Combine(directory.FullName, relative);
    }
}

public sealed class CapturingMail : IMailSender
{
    private readonly ConcurrentQueue<string> bodies = new();

    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        bodies.Enqueue(textBody);
        return Task.CompletedTask;
    }

    /// <summary>Waits (bounded) until at least <paramref name="count"/> messages were sent, then returns the last token.</summary>
    public async Task<string> LastTokenAsync(int count = 1, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (bodies.Count < count && Environment.TickCount64 < deadline) await Task.Delay(25);
        if (bodies.Count < count) throw new TimeoutException($"Expected {count} mail messages but captured {bodies.Count}.");
        return bodies.Last().Split("token: ", StringSplitOptions.None).Last().Trim();
    }
}
