using Microsoft.EntityFrameworkCore;
using Shouldly;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.Api.Tests;

[Collection("runtime-role")]
public sealed class RuntimeRoleWebhookTests(RuntimeRoleFixture fixture)
{
    [DockerFact]
    public async Task Provider_route_creates_receipt_and_outbox_under_runtime_scope()
    {
        var tenantId = Guid.NewGuid(); var channelId = Guid.NewGuid();
        await using (var owner = fixture.Context(fixture.OwnerConnection))
        {
            owner.Tenants.Add(new Tenant(tenantId, $"webhook-{tenantId:N}", "Webhook"));
            owner.Channels.Add(new Channel(channelId, tenantId, "whatsapp", $"phone-{tenantId:N}", true));
            owner.ProviderRoutes.Add(new ProviderRoute { Provider = "whatsapp", ProviderAssetId = $"phone-{tenantId:N}", TenantId = tenantId, ChannelId = channelId });
            await owner.SaveChangesAsync();
        }
        await using var db = fixture.Context(fixture.RuntimeConnection);
        var service = new WebhookService(db, new TenantExecutionScope(db));
        (await service.PersistByAssetAsync($"phone-{tenantId:N}", "event-1", "{}"u8.ToArray(), CancellationToken.None)).ShouldBeTrue();
        await new TenantExecutionScope(db).RunAsync(tenantId, async token =>
        {
            (await db.WebhookReceipts.CountAsync(token)).ShouldBe(1);
            (await db.Outbox.CountAsync(x => x.Type == "webhook.received", token)).ShouldBe(1);
        }, CancellationToken.None);
    }
}
