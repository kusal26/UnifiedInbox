using Shouldly;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.IntegrationTests;

public sealed class InboxStoreTests
{
    [Fact]
    public void Duplicate_external_message_is_ignored() { var store = new InMemoryInboxStore(); var tenant = store.Tenants.Single(); var conversation = store.EnsureConversation(tenant.Id); var before = store.Messages.Count; store.AddInbound(tenant.Id, conversation.Id, "same", "external-1"); store.AddInbound(tenant.Id, conversation.Id, "same", "external-1"); store.Messages.Count.ShouldBe(before + 1); }
    [Fact]
    public void Outbound_idempotency_returns_same_message() { var store = new InMemoryInboxStore(); var tenant = store.Tenants.Single(); var user = store.Users.First(); var conversation = store.EnsureConversation(tenant.Id); var first = store.AddOutbound(tenant.Id, conversation.Id, user.Id, "hello", "key-1"); var second = store.AddOutbound(tenant.Id, conversation.Id, user.Id, "hello", "key-1"); second.Id.ShouldBe(first.Id); store.Messages.Count(x => x.Id == first.Id).ShouldBe(1); }
}
