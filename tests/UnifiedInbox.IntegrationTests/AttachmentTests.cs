using Shouldly;
using UnifiedInbox.Infrastructure;

namespace UnifiedInbox.IntegrationTests;

public sealed class AttachmentTests
{
    [Fact]
    public void Attachment_claim_is_tenant_and_uploader_owned() { var store = new InMemoryInboxStore(); var tenant = store.Tenants.Single(); var user = store.Users.First(); var staged = store.StageAttachment(tenant.Id, user.Id, "receipt.pdf", "application/pdf", 1024); var claimed = store.ClaimAttachments(tenant.Id, user.Id, [staged.Id]); claimed.Single().Claimed.ShouldBeTrue(); Should.Throw<InvalidOperationException>(() => store.ClaimAttachments(tenant.Id, user.Id, [staged.Id])); }
}
