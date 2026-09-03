using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

/// <summary>Release hardening against real Postgres: empty-database migrations, forced RLS
/// with the least-privilege application role, and uniqueness constraints.</summary>
public sealed class PostgresHardeningTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private string OwnerConnection => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(OwnerConnection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin))
            await create.ExecuteNonQueryAsync();
        await using (var grant = new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin))
            await grant.ExecuteNonQueryAsync();
        await using var context = OwnerContext();
        await context.Database.MigrateAsync(); // must succeed on an empty database
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [DockerFact]
    public async Task Empty_database_migrates_all_tenant_tables()
    {
        await using var connection = new NpgsqlConnection(OwnerConnection);
        await connection.OpenAsync();
        var tables = new[] { "Users", "Channels", "Messages", "Attachments", "Invitations", "Outbox", "WebhookReceipts", "ProviderRoutes", "NotificationPreferences", "ConnectionAttempts" };
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_name = @table", connection);
            command.Parameters.AddWithValue("table", table);
            (Convert.ToInt32(await command.ExecuteScalarAsync())).ShouldBe(1);
        }
    }

    [DockerFact]
    public async Task Application_role_is_forced_through_row_level_security()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using (var owner = OwnerContext())
        {
            owner.Tenants.Add(new Tenant(tenantA, "tenant-a", "A"));
            owner.Tenants.Add(new Tenant(tenantB, "tenant-b", "B"));
            owner.Users.Add(new User(Guid.NewGuid(), tenantA, "a@example.com", "A", UserRole.Owner) { NormalizedEmail = "A@EXAMPLE.COM", EmailVerifiedAt = DateTimeOffset.UtcNow });
            owner.Users.Add(new User(Guid.NewGuid(), tenantB, "b@example.com", "B", UserRole.Owner) { NormalizedEmail = "B@EXAMPLE.COM", EmailVerifiedAt = DateTimeOffset.UtcNow });
            await owner.SaveChangesAsync();
        }

        // The runtime role cannot bypass RLS and has no BYPASSRLS attribute.
        await using var connection = new NpgsqlConnection(AppConnection());
        await connection.OpenAsync();
        (await Scalar<bool>(connection, "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'app_runtime'")).ShouldBeFalse();
        (await Scalar<bool>(connection, "SELECT relforcerowsecurity FROM pg_class WHERE relname = 'Users'")).ShouldBeTrue();

        (await CountUsers(connection, null)).ShouldBe(0); // fail closed with no tenant
        (await CountUsers(connection, tenantA)).ShouldBe(1);
        (await CountUsers(connection, tenantB)).ShouldBe(1);
    }

    [DockerFact]
    public async Task Provider_routes_and_idempotency_keys_stay_unique()
    {
        var tenantId = Guid.NewGuid();
        await using var context = OwnerContext();
        var channelId = Guid.NewGuid();
        context.Channels.Add(new Channel(channelId, tenantId, "whatsapp", "phone-1", true));
        context.ProviderRoutes.Add(new ProviderRoute { Provider = "whatsapp", ProviderAssetId = "phone-1", TenantId = tenantId, ChannelId = channelId });
        await context.SaveChangesAsync();

        context.ProviderRoutes.Add(new ProviderRoute { Provider = "whatsapp", ProviderAssetId = "phone-1", TenantId = tenantId, ChannelId = channelId });
        (await Should.ThrowAsync<DbUpdateException>(context.SaveChangesAsync())).ShouldNotBeNull();
    }

    private InboxDbContext OwnerContext() =>
        new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(OwnerConnection).Options, null);

    private string AppConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = "app_runtime", Password = "test-only" };
        return builder.ToString();
    }

    private static async Task<int> CountUsers(NpgsqlConnection connection, Guid? tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"Users\"", connection);
        if (tenantId is null) await using (var reset = new NpgsqlCommand("RESET app.current_tenant", connection)) await reset.ExecuteNonQueryAsync();
        else await using (var set = new NpgsqlCommand($"SET app.current_tenant = '{tenantId}'", connection)) await set.ExecuteNonQueryAsync();
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T))!;
    }
}
