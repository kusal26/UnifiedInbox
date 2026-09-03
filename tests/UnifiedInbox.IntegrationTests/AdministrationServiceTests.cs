using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

public sealed class AdministrationServiceTests
{
    [Fact]
    public async Task Role_matrix_is_enforced_from_database_membership()
    {
        var tenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (ownerDb, _) = TestContexts.Create(tenantId, Guid.NewGuid(), UserRole.Owner, dbName);
        TestContexts.SeedUser(ownerDb, tenantId, agentId, UserRole.Agent, "agent@example.com");
        var (agentDb, _) = TestContexts.Create(tenantId, agentId, UserRole.Agent, dbName);
        var agentAdmin = new AdministrationService(agentDb, new TestTenant(tenantId, agentId, UserRole.Agent));

        await Should.ThrowAsync<UnauthorizedAccessException>(agentAdmin.UsersAsync(CancellationToken.None));
        await Should.ThrowAsync<UnauthorizedAccessException>(agentAdmin.SetUserRoleAsync(agentId, UserRole.Admin, CancellationToken.None));
        (await agentAdmin.NotificationsAsync(false, CancellationToken.None)).ShouldBeEmpty(); // reads are allowed
    }

    [Fact]
    public async Task Owner_changes_roles_but_cannot_change_their_own()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        TestContexts.SeedUser(db, tenantId, agentId, UserRole.Agent, "agent@example.com");
        var admin = new AdministrationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner));

        (await admin.SetUserRoleAsync(agentId, UserRole.Admin, CancellationToken.None)).Role.ShouldBe(UserRole.Admin);
        var selfChange = await Should.ThrowAsync<InboxException>(admin.SetUserRoleAsync(ownerId, UserRole.Agent, CancellationToken.None));
        selfChange.Code.ShouldBe("cannot_change_own_role");
    }

    [Fact]
    public async Task Deactivation_revokes_sessions_while_admins_cannot_touch_owners()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner, dbName);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");
        var agent = TestContexts.SeedUser(db, tenantId, agentId, UserRole.Agent, "agent@example.com");
        db.RefreshTokens.Add(new RefreshToken { TenantId = tenantId, UserId = agentId, TokenHash = "hash-1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        db.SaveChanges();
        var ownerAdmin = new AdministrationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner));

        (await ownerAdmin.SetUserActiveAsync(agentId, false, CancellationToken.None)).IsActive.ShouldBeFalse();
        agent.IsActive.ShouldBeFalse();

        var (adminDb, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin, dbName);
        var adminAdmin = new AdministrationService(adminDb, new TestTenant(tenantId, adminId, UserRole.Admin));
        var forbidden = await Should.ThrowAsync<InboxException>(adminAdmin.SetUserActiveAsync(ownerId, false, CancellationToken.None));
        forbidden.StatusCode.ShouldBe(403);
        var selfBlock = await Should.ThrowAsync<InboxException>(adminAdmin.SetUserActiveAsync(adminId, false, CancellationToken.None));
        selfBlock.Code.ShouldBe("cannot_deactivate_self");

        var actions = db.AuditEntries.Select(x => x.Action).ToList();
        actions.ShouldContain("user.deactivated");
    }

    [Fact]
    public async Task Canned_responses_support_full_lifecycle_with_cross_tenant_denial()
    {
        var tenantA = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (dbA, _) = TestContexts.Create(tenantA, ownerA, UserRole.Owner, dbName);
        TestContexts.SeedUser(dbA, tenantA, ownerA, UserRole.Owner, "a@example.com");
        TestContexts.SeedUser(dbA, tenantB, ownerB, UserRole.Owner, "b@example.com");
        var adminA = new AdministrationService(dbA, new TestTenant(tenantA, ownerA, UserRole.Owner));

        var created = await adminA.AddCannedResponseAsync("Hello", "hi", "Hello there", CancellationToken.None);
        (await adminA.UpdateCannedResponseAsync(created.Id, "Hello!", "hi", "Hello there!", CancellationToken.None)).Title.ShouldBe("Hello!");
        (await adminA.DeleteCannedResponseAsync(created.Id, CancellationToken.None)).ShouldBeTrue();
        (await adminA.DeleteCannedResponseAsync(created.Id, CancellationToken.None)).ShouldBeFalse();

        var other = await adminA.AddCannedResponseAsync("Other", "o", "Other", CancellationToken.None);
        var (dbB, _) = TestContexts.Create(tenantB, ownerB, UserRole.Owner, dbName);
        var adminB = new AdministrationService(dbB, new TestTenant(tenantB, ownerB, UserRole.Owner));
        var missing = await Should.ThrowAsync<InboxException>(adminB.UpdateCannedResponseAsync(other.Id, "X", "x", "X", CancellationToken.None));
        missing.StatusCode.ShouldBe(404);
        (await adminB.DeleteCannedResponseAsync(other.Id, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Notifications_read_preferences_and_csv_export_behave()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        db.Notifications.Add(new NotificationEntity { TenantId = tenantId, Type = "message.received", Text = "Hello" });
        db.Notifications.Add(new NotificationEntity { TenantId = tenantId, Type = "channel.unhealthy", Text = "Down" });
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = ownerId, Action = "auth.login.failed", Resource = ownerId.ToString(), Metadata = "{\"reason\":\"bad-password\"}" });
        db.SaveChanges();
        var admin = new AdministrationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner));

        (await admin.NotificationsAsync(true, CancellationToken.None)).Count.ShouldBe(2);
        var first = (await admin.NotificationsAsync(false, CancellationToken.None)).First();
        (await admin.MarkNotificationReadAsync(first.Id, CancellationToken.None)).ShouldBeTrue();
        (await admin.NotificationsAsync(true, CancellationToken.None)).Count.ShouldBe(1);
        await admin.MarkAllNotificationsReadAsync(CancellationToken.None);
        (await admin.NotificationsAsync(true, CancellationToken.None)).ShouldBeEmpty();

        (await admin.SetNotificationPreferenceAsync("message.received", false, CancellationToken.None)).ShouldHaveSingleItem();
        (await admin.NotificationPreferencesAsync(CancellationToken.None)).Single().Enabled.ShouldBeFalse();
        await Should.ThrowAsync<ArgumentException>(admin.SetNotificationPreferenceAsync("nope", true, CancellationToken.None));

        var csv = await admin.AuditCsvAsync(null, CancellationToken.None);
        csv.ShouldStartWith("created_at,actor_id,action,resource,metadata\n");
        csv.ShouldContain("auth.login.failed");
    }

    [Fact]
    public async Task Overview_metrics_aggregate_the_requested_window()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        var channelId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        db.Channels.Add(new Channel(Guid.NewGuid(), tenantId, "whatsapp", "123", true) { DisplayName = "Sales" });
        db.SaveChanges();
        channelId = db.Channels.Single().Id;
        db.Contacts.Add(new Contact(contactId, tenantId, "whatsapp", "123", "cust-1", "Cust", "+15550001"));
        var recent = new Conversation { TenantId = tenantId, ChannelId = channelId, ContactId = contactId, ExternalConversationId = "recent", CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var old = new Conversation { TenantId = tenantId, ChannelId = channelId, ContactId = contactId, ExternalConversationId = "old", Status = ConversationStatus.Closed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), UpdatedAt = DateTimeOffset.UtcNow.AddDays(-60) };
        db.Conversations.AddRange(recent, old);
        db.Messages.AddRange(
            new Message { TenantId = tenantId, ChannelId = channelId, ConversationId = recent.Id, Direction = MessageDirection.Inbound, Body = "hi", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Message { TenantId = tenantId, ChannelId = channelId, ConversationId = old.Id, Direction = MessageDirection.Outbound, Body = "old", CreatedAt = DateTimeOffset.UtcNow.AddDays(-60) });
        db.InternalNotes.Add(new InternalNote { TenantId = tenantId, ConversationId = recent.Id, AuthorId = ownerId, Body = "note" });
        db.SaveChanges();
        var admin = new AdministrationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner));

        var week = await admin.OverviewMetricsAsync(7, CancellationToken.None);
        week.ConversationsOpened.ShouldBe(1);
        week.OpenConversations.ShouldBe(1);
        week.MessagesInbound.ShouldBe(1);
        week.MessagesOutbound.ShouldBe(0);
        week.NotesCreated.ShouldBe(1);

        var quarter = await admin.OverviewMetricsAsync(90, CancellationToken.None);
        quarter.ConversationsOpened.ShouldBe(2);
        quarter.MessagesOutbound.ShouldBe(1);

        await Should.ThrowAsync<ArgumentException>(admin.OverviewMetricsAsync(14, CancellationToken.None));
    }
}
