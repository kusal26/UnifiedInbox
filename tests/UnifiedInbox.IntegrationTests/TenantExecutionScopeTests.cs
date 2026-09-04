using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

public sealed class TenantExecutionScopeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private Guid tenantA;
    private Guid tenantB;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();

        await using (var owner = Context(container.GetConnectionString()))
        {
            await owner.Database.MigrateAsync();
            tenantA = Guid.NewGuid();
            tenantB = Guid.NewGuid();
            owner.Tenants.AddRange(new Tenant(tenantA, "tenant-a", "Tenant A"), new Tenant(tenantB, "tenant-b", "Tenant B"));
            owner.Users.AddRange(UserFor(tenantA, "a@example.com"), UserFor(tenantB, "b@example.com"));
            await owner.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [DockerFact]
    public async Task Runtime_role_without_context_reads_no_users()
    {
        await using var db = Context(RuntimeConnection());
        (await db.Users.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [DockerFact]
    public async Task Tenant_scope_reads_only_selected_tenant_and_allows_its_writes()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);

        await scope.RunAsync(tenantA, async token =>
        {
            (await db.Users.CountAsync(token)).ShouldBe(1);
            (await db.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(1);
            db.Users.Add(UserFor(tenantA, "second-a@example.com"));
            await db.SaveChangesAsync(token);
        }, CancellationToken.None);

        await scope.RunAsync(tenantB, async token =>
            (await db.Users.CountAsync(token)).ShouldBe(1), CancellationToken.None);
    }

    [DockerFact]
    public async Task Tenant_scope_rejects_cross_tenant_writes()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);

        await scope.RunAsync(tenantA, async token =>
        {
            db.Users.Add(UserFor(tenantB, "cross@example.com"));
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(token));
        }, CancellationToken.None);
    }

    [DockerFact]
    public async Task Disposing_scope_resets_pooled_connection_context()
    {
        var connection = RuntimeConnection();
        await using (var db = Context(connection))
        {
            var scope = new TenantExecutionScope(db);
            await scope.RunAsync(tenantA, async token => (await db.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(1), CancellationToken.None);
        }

        await using var fresh = Context(connection);
        (await fresh.Users.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [DockerFact]
    public async Task Nested_scope_for_another_tenant_throws()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);

        await scope.RunAsync(tenantA, async token =>
            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(tenantB, _ => Task.CompletedTask, token)), CancellationToken.None);
    }

    private InboxDbContext Context(string connection) =>
        new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);

    private string RuntimeConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Username = "app_runtime", Password = "test-only", Pooling = true };
        return builder.ConnectionString;
    }

    private static User UserFor(Guid tenantId, string email) =>
        new(Guid.NewGuid(), tenantId, email, email, UserRole.Agent)
        {
            NormalizedEmail = email.ToUpperInvariant(),
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = "test"
        };
}
