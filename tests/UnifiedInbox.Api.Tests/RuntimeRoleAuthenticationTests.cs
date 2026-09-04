using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.Api.Tests;

[CollectionDefinition("runtime-role")]
public sealed class RuntimeRoleCollection : ICollectionFixture<RuntimeRoleFixture>;

public sealed class RuntimeRoleFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public string OwnerConnection => container.GetConnectionString();
    public string RuntimeConnection { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(OwnerConnection); await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();
        await using var owner = Context(OwnerConnection); await owner.Database.MigrateAsync();
        RuntimeConnection = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = "app_runtime", Password = "test-only" }.ConnectionString;
    }

    public InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[Collection("runtime-role")]
public sealed class RuntimeRoleAuthenticationTests(RuntimeRoleFixture fixture)
{
    [DockerFact]
    public async Task Register_verify_login_and_refresh_rotation_run_as_app_runtime()
    {
        await using var db = fixture.Context(fixture.RuntimeConnection);
        var mail = new CapturingMail();
        var auth = new AuthenticationService(db, new PasswordHasher<User>(), new FakeIssuer(), new AnonymousTenant(), mail, new TenantExecutionScope(db));
        await auth.RegisterAsync(new Registration("Runtime", "runtime-auth", "Owner", "runtime@example.com", "supersecure-password-1"), CancellationToken.None);
        var verification = mail.LastToken();
        TenantToken.TryGetTenantId(verification, out _).ShouldBeTrue();
        (await auth.VerifyEmailAsync(verification, CancellationToken.None)).ShouldBeTrue();
        var login = await auth.LoginAsync("runtime-auth", "runtime@example.com", "supersecure-password-1", CancellationToken.None);
        login.ShouldNotBeNull();
        TenantToken.TryGetTenantId(login.RefreshToken, out _).ShouldBeTrue();
        var rotated = await auth.RefreshAsync(login.RefreshToken, CancellationToken.None);
        rotated.ShouldNotBeNull();
        var reuse = await Should.ThrowAsync<InboxException>(() => auth.RefreshAsync(login.RefreshToken, CancellationToken.None));
        reuse.Code.ShouldBe("token_reuse_detected");
    }

    private sealed class AnonymousTenant : ICurrentTenant { public Guid? TenantId => null; public Guid? UserId => null; public UserRole? Role => null; }
    private sealed class FakeIssuer : ITokenIssuer { public (string Token, DateTimeOffset ExpiresAt) Issue(User user) => ($"access-{user.Id}", DateTimeOffset.UtcNow.AddMinutes(15)); }
    private sealed class CapturingMail : IMailSender
    {
        private readonly List<string> bodies = [];
        public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken) { bodies.Add(textBody); return Task.CompletedTask; }
        public string LastToken() => bodies[^1].Split("token: ").Last().Trim();
    }
}
