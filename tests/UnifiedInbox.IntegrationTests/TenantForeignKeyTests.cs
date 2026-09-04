using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

[CollectionDefinition("tenant-fk")]
public sealed class TenantForeignKeyCollection : ICollectionFixture<TenantForeignKeyFixture>;

/// <summary>Shared PostgreSQL for the tenant-aware foreign-key suite (migrated to the latest schema).</summary>
public sealed class TenantForeignKeyFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public string OwnerConnection => container.GetConnectionString();
    public string RuntimeConnection { get; private set; } = "";

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
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
    public InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}

/// <summary>
/// Every tenant-scoped dependent must be tied to its parent by a composite (TenantId, id)
/// foreign key, so a row stamped with tenant A can never reference a parent owned by tenant B.
/// Each attempt must fail with PostgreSQL error 23503 (foreign_key_violation).
/// </summary>
[Collection("tenant-fk")]
public sealed class TenantForeignKeyTests(TenantForeignKeyFixture fixture)
{
    [DockerFact]
    public async Task Channel_credential_cannot_reference_another_tenants_channel() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.ChannelCredentials.Add(new ChannelCredential { TenantId = tenantA, ChannelId = b.ChannelB, EncryptedAccessToken = "x", EncryptedWebhookSecret = "x" });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Conversation_cannot_reference_another_tenants_channel_or_contact() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.Conversations.Add(new Conversation { TenantId = tenantA, ChannelId = b.ChannelB, ContactId = b.ContactB, ExternalConversationId = "x-conv" });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Message_cannot_reference_another_tenants_channel_conversation_or_sender() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.Messages.Add(new Message { TenantId = tenantA, ChannelId = b.ChannelB, ConversationId = b.ConversationB, SenderUserId = b.UserB, Direction = MessageDirection.Outbound, Body = "hello", Status = MessageStatus.Pending, Sequence = 1 });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Note_cannot_reference_another_tenants_conversation_or_author() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.InternalNotes.Add(new InternalNote { TenantId = tenantA, ConversationId = b.ConversationB, AuthorId = b.UserB, Body = "note" });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Attachment_cannot_reference_another_tenants_uploader_or_message() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.Attachments.Add(new Attachment { TenantId = tenantA, UploaderId = b.UserB, MessageId = b.MessageB, ObjectKey = "k", FileName = "f.pdf", ContentType = "application/pdf", Size = 1 });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Health_cannot_reference_another_tenants_channel() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.ChannelHealth.Add(new ChannelHealth { TenantId = tenantA, ChannelId = b.ChannelB, Reason = "x" });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Notification_preference_cannot_reference_another_tenants_user() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.NotificationPreferences.Add(new NotificationPreference { TenantId = tenantA, UserId = b.UserB, Kind = "message.received" });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Refresh_token_cannot_reference_another_tenants_user() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.RefreshTokens.Add(new RefreshToken { TenantId = tenantA, UserId = b.UserB, TokenHash = "hash-" + Guid.NewGuid().ToString("N"), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Invitation_cannot_reference_another_tenants_inviter() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.Invitations.Add(new Invitation { TenantId = tenantA, Email = "x@example.com", Role = UserRole.Agent, TokenHash = "hash-" + Guid.NewGuid().ToString("N"), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), InvitedById = b.UserB });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Connection_attempt_cannot_reference_another_tenants_user_or_channel() =>
        await AssertCrossTenantRejectedAsync(async (db, tenantA, b, token) =>
        {
            db.ConnectionAttempts.Add(new ConnectionAttempt { TenantId = tenantA, ChannelId = b.ChannelB, InitiatingUserId = b.UserB, StateHash = "state-" + Guid.NewGuid().ToString("N"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync(token);
        });

    [DockerFact]
    public async Task Valid_same_tenant_references_are_still_allowed() =>
        await WithHarnessAsync(async (db, tenantA, b) =>
        {
            await new TenantExecutionScope(db).RunAsync(tenantA, async token =>
            {
                db.Messages.Add(new Message { TenantId = tenantA, ChannelId = b.ChannelA, ConversationId = b.ConversationA, SenderUserId = b.UserA, Direction = MessageDirection.Outbound, Body = "hello", Status = MessageStatus.Pending, Sequence = 2 });
                db.RefreshTokens.Add(new RefreshToken { TenantId = tenantA, UserId = b.UserA, TokenHash = "hash-ok-" + Guid.NewGuid().ToString("N"), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
                await db.SaveChangesAsync(token); // must not throw
            }, CancellationToken.None);
        });

    private Task AssertCrossTenantRejectedAsync(Func<InboxDbContext, Guid, SeedData, CancellationToken, Task> action) =>
        WithHarnessAsync(async (db, tenantA, b) =>
        {
            await new TenantExecutionScope(db).RunAsync(tenantA, async token =>
            {
                var exception = await Should.ThrowAsync<DbUpdateException>(() => action(db, tenantA, b, token));
                var postgres = exception.InnerException as PostgresException;
                postgres.ShouldNotBeNull();
                postgres.SqlState.ShouldBe("23503");
            }, CancellationToken.None);
        });

    private async Task WithHarnessAsync(Func<InboxDbContext, Guid, SeedData, Task> body)
    {
        var data = await SeedAsync();
        await using var db = data.Db;
        await body(db, data.Data.TenantA, data.Data);
    }

    private async Task<Harness> SeedAsync()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var channelA = Guid.NewGuid();
        var channelB = Guid.NewGuid();
        var contactA = Guid.NewGuid();
        var contactB = Guid.NewGuid();
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var messageB = Guid.NewGuid();

        await using (var owner = fixture.Context(fixture.OwnerConnection))
        {
            owner.Tenants.AddRange(new Tenant(tenantA, "fk-a-" + tenantA.ToString("N")[..6], "A"), new Tenant(tenantB, "fk-b-" + tenantB.ToString("N")[..6], "B"));
            owner.Users.AddRange(User(userA, tenantA), User(userB, tenantB));
            owner.Channels.AddRange(new Channel(channelA, tenantA, "whatsapp", "phone-a-" + tenantA.ToString("N"), true), new Channel(channelB, tenantB, "whatsapp", "phone-b-" + tenantB.ToString("N"), true));
            owner.Contacts.AddRange(Contact(contactA, tenantA, "phone-a-" + tenantA.ToString("N")), Contact(contactB, tenantB, "phone-b-" + tenantB.ToString("N")));
            owner.Conversations.AddRange(new Conversation { Id = conversationA, TenantId = tenantA, ChannelId = channelA, ContactId = contactA, ExternalConversationId = "cust-a" }, new Conversation { Id = conversationB, TenantId = tenantB, ChannelId = channelB, ContactId = contactB, ExternalConversationId = "cust-b" });
            owner.Messages.Add(new Message { Id = messageB, TenantId = tenantB, ChannelId = channelB, ConversationId = conversationB, Direction = MessageDirection.Inbound, Body = "b", Status = MessageStatus.Delivered, Sequence = 1 });
            await owner.SaveChangesAsync();
        }

        return new Harness(fixture.Context(fixture.RuntimeConnection), new SeedData(tenantA, tenantB, userA, userB, channelA, channelB, contactA, contactB, conversationA, conversationB, messageB));
    }

    private static User User(Guid id, Guid tenantId) => new(id, tenantId, id.ToString("N") + "@example.com", "User", UserRole.Agent)
    {
        NormalizedEmail = (id.ToString("N") + "@example.com").ToUpperInvariant(),
        EmailVerifiedAt = DateTimeOffset.UtcNow,
        PasswordHash = "test",
    };

    private static Contact Contact(Guid id, Guid tenantId, string account) => new(id, tenantId, "whatsapp", account, "cust", "Contact", "+15550001");

    private sealed record Harness(InboxDbContext Db, SeedData Data);
    private sealed record SeedData(Guid TenantA, Guid TenantB, Guid UserA, Guid UserB, Guid ChannelA, Guid ChannelB, Guid ContactA, Guid ContactB, Guid ConversationA, Guid ConversationB, Guid MessageB);
}

/// <summary>
/// Validates the upgrade path: a database migrated to the pre-foreign-key schema with existing
/// consistent rows must accept the additive TenantAwareForeignKeys migration, after which
/// cross-tenant references are rejected.
/// </summary>
public sealed class TenantForeignKeyMigrationUpgradeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private string ownerConnection = "";
    private string runtimeConnection = "";
    private string databaseName = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand("CREATE ROLE unified_inbox NOLOGIN", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("CREATE ROLE app_runtime WITH LOGIN NOBYPASSRLS PASSWORD 'test-only'", admin).ExecuteNonQueryAsync();
        await new NpgsqlCommand("GRANT CONNECT ON DATABASE postgres TO app_runtime", admin).ExecuteNonQueryAsync();

        databaseName = "upgrade_" + Guid.NewGuid().ToString("N")[..10];
        await new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin).ExecuteNonQueryAsync();
        ownerConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = databaseName }.ConnectionString;
        await new NpgsqlCommand($"GRANT CONNECT ON DATABASE \"{databaseName}\" TO app_runtime", admin).ExecuteNonQueryAsync();
        runtimeConnection = new NpgsqlConnectionStringBuilder(ownerConnection) { Username = "app_runtime", Password = "test-only", Pooling = true }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
        await container.DisposeAsync();
    }

    [DockerFact]
    public async Task Existing_consistent_rows_survive_the_tenant_foreign_key_migration()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var channelA = Guid.NewGuid();
        var channelB = Guid.NewGuid();
        var contactA = Guid.NewGuid();
        var conversationA = Guid.NewGuid();
        var messageA = Guid.NewGuid();

        await using (var context = Context(ownerConnection))
        {
            await context.Database.MigrateAsync("20260903162613_PhaseFAppRoleGrants");
            context.Tenants.AddRange(new Tenant(tenantA, "upgrade-a", "A"), new Tenant(tenantB, "upgrade-b", "B"));
            context.Users.Add(new User(userA, tenantA, "owner@example.com", "Owner", UserRole.Owner) { NormalizedEmail = "OWNER@EXAMPLE.COM", PasswordHash = "x" });
            context.Channels.AddRange(new Channel(channelA, tenantA, "whatsapp", "phone-up", true), new Channel(channelB, tenantB, "whatsapp", "phone-other", true));
            context.Contacts.Add(new Contact(contactA, tenantA, "whatsapp", "phone-up", "cust", "C", "+1555"));
            context.Conversations.Add(new Conversation { Id = conversationA, TenantId = tenantA, ChannelId = channelA, ContactId = contactA, ExternalConversationId = "cust" });
            context.Messages.Add(new Message { Id = messageA, TenantId = tenantA, ChannelId = channelA, ConversationId = conversationA, Direction = MessageDirection.Inbound, Body = "hi", Status = MessageStatus.Delivered, Sequence = 1 });
            context.RefreshTokens.Add(new RefreshToken { TenantId = tenantA, UserId = userA, TokenHash = "h", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            await context.SaveChangesAsync();
            await context.Database.MigrateAsync(); // applies TenantAwareForeignKeys over existing rows
        }

        await using var db = Context(runtimeConnection);
        var cross = await Should.ThrowAsync<DbUpdateException>(() => new TenantExecutionScope(db).RunAsync(tenantA, async token =>
        {
            db.ChannelCredentials.Add(new ChannelCredential { TenantId = tenantA, ChannelId = channelB, EncryptedAccessToken = "x", EncryptedWebhookSecret = "x" });
            await db.SaveChangesAsync(token);
        }, CancellationToken.None));
        (cross.InnerException as PostgresException)?.SqlState.ShouldBe("23503");
    }

    private InboxDbContext Context(string connection) => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql(connection).Options);
}
