using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Proves tenant identity and invisibility through the <c>app_runtime</c> role against real
/// Postgres: no ambient context reads nothing, a scoped transaction is confined to its tenant,
/// committed writes are visible to later scopes (and to no other tenant), disposal resets the
/// pooled connection, and cross-tenant writes/nesting are rejected.
/// </summary>
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
        (await db.Users.CountAsync()).ShouldBe(0);
        (await db.Users.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [DockerFact]
    public async Task Scope_reports_identity_and_reads_only_the_selected_tenant()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);
        scope.CurrentTenantId.ShouldBeNull();

        await scope.RunAsync(tenantA, async token =>
        {
            scope.CurrentTenantId.ShouldBe(tenantA);
            (await db.Users.CountAsync(token)).ShouldBe(1);
            (await db.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(1);
        }, CancellationToken.None);

        scope.CurrentTenantId.ShouldBeNull(); // cleared after the scope
        await scope.RunAsync(tenantB, async token =>
        {
            scope.CurrentTenantId.ShouldBe(tenantB);
            (await db.Users.CountAsync(token)).ShouldBe(1);
            (await db.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(1);
        }, CancellationToken.None);
    }

    [DockerFact]
    public async Task Committed_scope_write_is_read_back_by_a_fresh_connection_but_not_other_tenants()
    {
        var email = $"second-{Guid.NewGuid():N}@example.com";
        await using (var db = Context(RuntimeConnection()))
        {
            var scope = new TenantExecutionScope(db);
            await scope.RunAsync(tenantA, async token =>
            {
                db.Users.Add(UserFor(tenantA, email));
                await db.SaveChangesAsync(token);
            }, CancellationToken.None);
        }

        await using var fresh = Context(RuntimeConnection());
        var second = new TenantExecutionScope(fresh);
        await second.RunAsync(tenantA, async token =>
        {
            (await fresh.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(2); // original + committed write
            (await fresh.Users.AnyAsync(x => x.NormalizedEmail == email.ToUpperInvariant(), token)).ShouldBeTrue();
        }, CancellationToken.None);
        await second.RunAsync(tenantB, async token =>
        {
            (await fresh.Users.IgnoreQueryFilters().CountAsync(token)).ShouldBe(1); // tenant B never sees A's write
        }, CancellationToken.None);
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
    public async Task Nested_scope_for_another_tenant_throws_but_same_tenant_is_allowed()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);

        await scope.RunAsync(tenantA, async token =>
        {
            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(tenantB, _ => Task.CompletedTask, token));
            await scope.RunAsync(tenantA, async inner => (await db.Users.CountAsync(inner)).ShouldBe(1), token);
        }, CancellationToken.None);
    }

    [DockerFact]
    public async Task Empty_tenant_id_is_rejected()
    {
        await using var db = Context(RuntimeConnection());
        var scope = new TenantExecutionScope(db);
        await Should.ThrowAsync<ArgumentException>(() => scope.RunAsync(Guid.Empty, _ => Task.CompletedTask, CancellationToken.None));
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
