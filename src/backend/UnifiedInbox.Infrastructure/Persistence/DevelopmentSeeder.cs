using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Infrastructure.Persistence;

public static class DevelopmentSeeder
{
    public static async Task SeedAsync(InboxDbContext db, IPasswordHasher<User> passwords)
    {
        if (await db.Tenants.AnyAsync()) return;
        var tenant = new Tenant(Guid.Parse("11111111-1111-1111-1111-111111111111"), "acme", "Acme Workspace");
        var owner = new User(Guid.Parse("33333333-3333-3333-3333-333333333333"), tenant.Id, "owner@acme.test", "Olivia Owner", UserRole.Owner) { NormalizedEmail = "OWNER@ACME.TEST", EmailVerifiedAt = DateTimeOffset.UtcNow };
        owner.PasswordHash = passwords.HashPassword(owner, "Development!123");
        var channel = new Channel(Guid.Parse("44444444-4444-4444-4444-444444444444"), tenant.Id, "whatsapp", "business-acme") { DisplayName = "Acme WhatsApp" };
        var contact = new Contact(Guid.Parse("55555555-5555-5555-5555-555555555555"), tenant.Id, "whatsapp", "business-acme", "customer-1", "Jamie Customer", "+15550000001");
        var conversation = new Conversation { TenantId = tenant.Id, ChannelId = channel.Id, ContactId = contact.Id, ExternalConversationId = "customer-1" };
        var message = new Message { TenantId = tenant.Id, ChannelId = channel.Id, ConversationId = conversation.Id, Direction = MessageDirection.Inbound, Body = "Welcome to the shared inbox", ExternalMessageId = "seed-1", Sequence = 1 };
        conversation.RecordInboundActivity(message.CreatedAt);
        var route = new ProviderRoute { Provider = "whatsapp", ProviderAssetId = "business-acme", TenantId = tenant.Id, ChannelId = channel.Id };
        db.AddRange(tenant, owner, channel, contact, conversation, message, route); await db.SaveChangesAsync();
    }
}
