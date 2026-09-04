using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

public sealed class ChannelServiceTests
{
    private static readonly string MasterKey = Convert.ToBase64String(SHA256.HashData("test-master-key"u8.ToArray()));
    private static readonly string OldMasterKey = Convert.ToBase64String(SHA256.HashData("old-master-key"u8.ToArray()));

    [Fact]
    public async Task Full_connect_flow_creates_channel_route_credential_and_health()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin);
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");
        var graph = new FakeGraph();
        var service = CreateService(db, tenantId, adminId, UserRole.Admin, graph);

        var attempt = await service.BeginConnectAsync("Sales", CancellationToken.None);
        attempt.State.ShouldNotBeNullOrWhiteSpace();
        (attempt.ExpiresAt - DateTimeOffset.UtcNow).ShouldBeLessThan(TimeSpan.FromMinutes(11));

        var channel = await service.CompleteConnectAsync(attempt.State, "auth-code", "phone-1", "waba-1", "Sales", CancellationToken.None);
        channel.ExternalAccountId.ShouldBe("phone-1");
        channel.IsHealthy.ShouldBeTrue();
        channel.Status.ShouldBe("connected");
        graph.SubscribeCalls.ShouldBe(1);

        db.Channels.IgnoreQueryFilters().ShouldHaveSingleItem();
        var route = db.ProviderRoutes.Single();
        route.Provider.ShouldBe("whatsapp");
        route.ProviderAssetId.ShouldBe("phone-1");
        route.TenantId.ShouldBe(tenantId);
        route.ChannelId.ShouldBe(channel.Id);
        var credential = db.ChannelCredentials.IgnoreQueryFilters().ShouldHaveSingleItem();
        new CredentialProtector(Convert.FromBase64String(MasterKey)).Unprotect(credential.EncryptedAccessToken).ShouldBe("graph-token");
        db.ChannelHealth.IgnoreQueryFilters().ShouldHaveSingleItem().Reason.ShouldBe("connected");
        db.Outbox.IgnoreQueryFilters().Select(x => x.Type).ShouldContain("channel.updated");
        db.AuditEntries.Select(x => x.Action).ShouldContain("channel.connected");

        // Health history is readable and the test probe succeeds.
        (await service.HealthHistoryAsync(channel.Id, CancellationToken.None)).ShouldHaveSingleItem();
        (await service.TestChannelAsync(channel.Id, CancellationToken.None)).Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task Connection_state_is_single_use_expiring_and_user_bound()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (db, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin, dbName);
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");
        TestContexts.SeedUser(db, tenantId, otherId, UserRole.Admin, "other@example.com");
        var service = CreateService(db, tenantId, adminId, UserRole.Admin, new FakeGraph());

        var attempt = await service.BeginConnectAsync("Sales", CancellationToken.None);
        await service.CompleteConnectAsync(attempt.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None);
        var replay = await Should.ThrowAsync<InboxException>(service.CompleteConnectAsync(attempt.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None));
        replay.Code.ShouldBe("invalid_state");

        var fresh = await service.BeginConnectAsync("Sales", CancellationToken.None);
        var (otherDb, _) = TestContexts.Create(tenantId, otherId, UserRole.Admin, dbName);
        var otherService = CreateService(otherDb, tenantId, otherId, UserRole.Admin, new FakeGraph());
        var bound = await Should.ThrowAsync<InboxException>(otherService.CompleteConnectAsync(fresh.State, "code", "phone-2", "waba-1", "Sales", CancellationToken.None));
        bound.Code.ShouldBe("invalid_state");

        var expiring = await service.BeginConnectAsync("Sales", CancellationToken.None);
        var stored = await db.ConnectionAttempts.SingleAsync(x => x.StateHash == ChannelService.Hash(expiring.State));
        stored.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var expired = await Should.ThrowAsync<InboxException>(service.CompleteConnectAsync(expiring.State, "code", "phone-3", "waba-1", "Sales", CancellationToken.None));
        expired.Code.ShouldBe("invalid_state");
    }

    [Fact]
    public async Task Missing_scopes_unverified_phones_and_failed_subscriptions_abort_connect()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin);
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");

        var noScopes = CreateService(db, tenantId, adminId, UserRole.Admin, new FakeGraph { Scopes = ["whatsapp_business_messaging"] });
        var attempt = await noScopes.BeginConnectAsync("Sales", CancellationToken.None);
        var scopes = await Should.ThrowAsync<InboxException>(noScopes.CompleteConnectAsync(attempt.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None));
        scopes.Code.ShouldBe("scopes_missing");

        var unverified = CreateService(db, tenantId, adminId, UserRole.Admin, new FakeGraph { PhoneStatus = "PENDING" });
        var attempt2 = await unverified.BeginConnectAsync("Sales", CancellationToken.None);
        var phone = await Should.ThrowAsync<InboxException>(unverified.CompleteConnectAsync(attempt2.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None));
        phone.Code.ShouldBe("phone_not_verified");

        var noSub = CreateService(db, tenantId, adminId, UserRole.Admin, new FakeGraph { SubscribeFails = true });
        var attempt3 = await noSub.BeginConnectAsync("Sales", CancellationToken.None);
        var sub = await Should.ThrowAsync<InboxException>(noSub.CompleteConnectAsync(attempt3.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None));
        sub.Code.ShouldBe("subscription_failed");

        db.Channels.IgnoreQueryFilters().ShouldBeEmpty();
        db.Notifications.Select(x => x.Type).ShouldContain("channel.unhealthy");
    }

    [Fact]
    public async Task Phone_numbers_cannot_be_shared_across_tenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (dbA, _) = TestContexts.Create(tenantA, ownerA, UserRole.Admin, dbName);
        TestContexts.SeedUser(dbA, tenantA, ownerA, UserRole.Admin, "a@example.com");
        TestContexts.SeedUser(dbA, tenantB, ownerB, UserRole.Admin, "b@example.com");
        var serviceA = CreateService(dbA, tenantA, ownerA, UserRole.Admin, new FakeGraph());
        var attemptA = await serviceA.BeginConnectAsync("Sales", CancellationToken.None);
        await serviceA.CompleteConnectAsync(attemptA.State, "code", "shared-phone", "waba-1", "Sales", CancellationToken.None);

        var (dbB, _) = TestContexts.Create(tenantB, ownerB, UserRole.Admin, dbName);
        var serviceB = CreateService(dbB, tenantB, ownerB, UserRole.Admin, new FakeGraph());
        var attemptB = await serviceB.BeginConnectAsync("Sales", CancellationToken.None);
        var conflict = await Should.ThrowAsync<InboxException>(serviceB.CompleteConnectAsync(attemptB.State, "code", "shared-phone", "waba-1", "Sales", CancellationToken.None));
        conflict.Code.ShouldBe("number_in_use");
    }

    [Fact]
    public async Task Disconnect_destroys_ciphertext_but_retains_history()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin);
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");
        var graph = new FakeGraph();
        var service = CreateService(db, tenantId, adminId, UserRole.Admin, graph);
        var attempt = await service.BeginConnectAsync("Sales", CancellationToken.None);
        var channel = await service.CompleteConnectAsync(attempt.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None);

        var contact = new Contact(Guid.NewGuid(), tenantId, "whatsapp", "phone-1", "cust", "Cust", "+1");
        db.Contacts.Add(contact);
        var conversation = new Conversation { TenantId = tenantId, ChannelId = channel.Id, ContactId = contact.Id, ExternalConversationId = "cust" };
        db.Conversations.Add(conversation);
        db.Messages.Add(new Message { TenantId = tenantId, ChannelId = channel.Id, ConversationId = conversation.Id, Direction = MessageDirection.Inbound, Body = "hi", Sequence = 1 });
        db.SaveChanges();

        await service.DisconnectAsync(channel.Id, CancellationToken.None);
        graph.UnsubscribeCalls.ShouldBe(1);
        db.ChannelCredentials.IgnoreQueryFilters().ShouldBeEmpty();
        db.ProviderRoutes.ShouldBeEmpty();
        db.Messages.IgnoreQueryFilters().ShouldHaveSingleItem(); // history retained
        var torn = db.Channels.IgnoreQueryFilters().Single();
        torn.Status.ShouldBe("disconnected");
        torn.IsEnabled.ShouldBeFalse();
        db.Notifications.Select(x => x.Text).ShouldContain(t => t.Contains("disconnected"));
    }

    [Fact]
    public async Task Revoked_provider_access_marks_the_channel_unhealthy()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin);
        TestContexts.SeedUser(db, tenantId, adminId, UserRole.Admin, "admin@example.com");
        var graph = new FakeGraph();
        var service = CreateService(db, tenantId, adminId, UserRole.Admin, graph);
        var attempt = await service.BeginConnectAsync("Sales", CancellationToken.None);
        var channel = await service.CompleteConnectAsync(attempt.State, "code", "phone-1", "waba-1", "Sales", CancellationToken.None);

        graph.Revoked = true;
        var result = await service.TestChannelAsync(channel.Id, CancellationToken.None);
        result.Healthy.ShouldBeFalse();
        db.Channels.Single().IsHealthy.ShouldBeFalse();
        db.Notifications.Select(x => x.Text).ShouldContain(t => t.Contains("revoked"));
        (await service.HealthHistoryAsync(channel.Id, CancellationToken.None)).Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Credential_rotation_reencrypts_with_the_new_master_key()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        var oldOnly = new DictionaryConfiguration(new Dictionary<string, string?> { ["Credentials:MasterKey"] = OldMasterKey });
        var channelId = Guid.NewGuid();
        db.Channels.Add(new Channel(channelId, tenantId, "whatsapp", "phone-1", true));
        db.ChannelCredentials.Add(new ChannelCredential { TenantId = tenantId, ChannelId = channelId, EncryptedAccessToken = new CredentialProtector(Convert.FromBase64String(OldMasterKey)).Protect("legacy-token") });
        db.SaveChanges();

        var rotated = new DictionaryConfiguration(new Dictionary<string, string?>
        {
            ["Credentials:MasterKey"] = MasterKey,
            ["Credentials:PreviousMasterKey"] = OldMasterKey,
        });
        var service = new ChannelService(db, new TestTenant(tenantId, ownerId, UserRole.Owner), new FakeGraph(), rotated);
        (await service.RotateCredentialsAsync(CancellationToken.None)).ShouldBe(1);
        var credential = db.ChannelCredentials.IgnoreQueryFilters().Single();
        credential.KeyVersion.ShouldBe(2);
        new CredentialProtector(Convert.FromBase64String(MasterKey)).Unprotect(credential.EncryptedAccessToken).ShouldBe("legacy-token");
    }

    [Fact]
    public async Task Agents_cannot_manage_channels_and_tenants_stay_isolated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (dbA, _) = TestContexts.Create(tenantA, agentId, UserRole.Agent, dbName);
        TestContexts.SeedUser(dbA, tenantA, agentId, UserRole.Agent, "agent@example.com");
        TestContexts.SeedUser(dbA, tenantB, ownerB, UserRole.Owner, "b@example.com");
        var agentService = CreateService(dbA, tenantA, agentId, UserRole.Agent, new FakeGraph());
        await Should.ThrowAsync<UnauthorizedAccessException>(agentService.BeginConnectAsync("Sales", CancellationToken.None));

        var (dbB, _) = TestContexts.Create(tenantB, ownerB, UserRole.Owner, dbName);
        var serviceB = CreateService(dbB, tenantB, ownerB, UserRole.Owner, new FakeGraph());
        var attempt = await serviceB.BeginConnectAsync("Sales", CancellationToken.None);
        var channel = await serviceB.CompleteConnectAsync(attempt.State, "code", "phone-b", "waba-b", "Sales", CancellationToken.None);
        await Should.ThrowAsync<UnauthorizedAccessException>(agentService.TestChannelAsync(channel.Id, CancellationToken.None));
    }

    private static ChannelService CreateService(InboxDbContext db, Guid tenantId, Guid userId, UserRole role, FakeGraph graph)
    {
        var configuration = new DictionaryConfiguration(new Dictionary<string, string?> { ["Credentials:MasterKey"] = MasterKey });
        return new(db, new TestTenant(tenantId, userId, role), graph, configuration);
    }

    private sealed class FakeGraph : IWhatsAppGraphClient
    {
        public List<string> Scopes { get; init; } = ["whatsapp_business_messaging", "whatsapp_business_management"];
        public string PhoneStatus { get; init; } = "VERIFIED";
        public bool SubscribeFails { get; init; }
        public bool Revoked { get; set; }
        public int SubscribeCalls { get; private set; }
        public int UnsubscribeCalls { get; private set; }

        public Task<string> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => Task.FromResult("graph-token");
        public Task<GraphPhoneNumber> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
        {
            if (Revoked) throw new InboxException("provider_unauthorized", "revoked", 502);
            return Task.FromResult(new GraphPhoneNumber(phoneNumberId, "+15550001", PhoneStatus));
        }
        public Task<string> GetBusinessNameAsync(string businessId, string accessToken, CancellationToken cancellationToken) => Task.FromResult("Acme WABA");
        public Task<IReadOnlyList<string>> GetTokenScopesAsync(string accessToken, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Scopes);
        public Task SubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            if (SubscribeFails) throw new InboxException("provider_error", "subscription refused", 502);
            SubscribeCalls++;
            return Task.CompletedTask;
        }
        public Task UnsubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            UnsubscribeCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<WhatsAppTemplateInfo>> ListMessageTemplatesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WhatsAppTemplateInfo>>([]);
    }
}
