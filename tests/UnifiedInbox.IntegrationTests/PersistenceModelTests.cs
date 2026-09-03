using Microsoft.EntityFrameworkCore;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Every_tenant_entity_has_a_query_filter()
    {
        using var db = CreateContext();
        var missing = db.Model.GetEntityTypes().Where(type => typeof(ITenantScoped).IsAssignableFrom(type.ClrType) && !type.GetDeclaredQueryFilters().Any()).Select(type => type.Name).ToArray();
        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Provider_and_idempotency_keys_are_database_unique()
    {
        using var db = CreateContext(); var message = db.Model.FindEntityType(typeof(Message))!;
        message.GetIndexes().Any(index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual([nameof(Message.TenantId), nameof(Message.ChannelId), nameof(Message.ExternalMessageId)])).ShouldBeTrue();
        message.GetIndexes().Any(index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual([nameof(Message.TenantId), nameof(Message.ConversationId), nameof(Message.IdempotencyKey)])).ShouldBeTrue();
    }

    private static InboxDbContext CreateContext() => new(new DbContextOptionsBuilder<InboxDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options, new TestTenant());
    private sealed class TestTenant : ICurrentTenant { public Guid? TenantId => Guid.NewGuid(); public Guid? UserId => Guid.NewGuid(); public UserRole? Role => UserRole.Owner; }
}
