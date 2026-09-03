using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Infrastructure.Persistence;

public sealed class InboxDbContext(DbContextOptions<InboxDbContext> options, ICurrentTenant? currentTenant = null) : DbContext(options)
{
    private Guid? CurrentTenantId => currentTenant?.TenantId;
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelCredential> ChannelCredentials => Set<ChannelCredential>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<InternalNote> InternalNotes => Set<InternalNote>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<CannedResponseEntity> CannedResponses => Set<CannedResponseEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<global::UnifiedInbox.Domain.WebhookReceipt> WebhookReceipts => Set<global::UnifiedInbox.Domain.WebhookReceipt>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>().HasKey(x => x.Id);
        modelBuilder.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        ConfigureTenant<User>(modelBuilder); ConfigureTenant<Channel>(modelBuilder); ConfigureTenant<ChannelCredential>(modelBuilder);
        ConfigureTenant<Contact>(modelBuilder); ConfigureTenant<Conversation>(modelBuilder); ConfigureTenant<Message>(modelBuilder);
        ConfigureTenant<InternalNote>(modelBuilder); ConfigureTenant<RefreshToken>(modelBuilder); ConfigureTenant<Invitation>(modelBuilder);
        ConfigureTenant<Attachment>(modelBuilder); ConfigureTenant<CannedResponseEntity>(modelBuilder); ConfigureTenant<NotificationEntity>(modelBuilder);
        ConfigureTenant<AuditEntryEntity>(modelBuilder); ConfigureTenant<global::UnifiedInbox.Domain.WebhookReceipt>(modelBuilder); ConfigureTenant<OutboxEvent>(modelBuilder);
        modelBuilder.Entity<User>().HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique();
        modelBuilder.Entity<Channel>().HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId }).IsUnique();
        modelBuilder.Entity<ChannelCredential>().HasIndex(x => x.ChannelId).IsUnique();
        modelBuilder.Entity<Contact>().HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId, x.ExternalCustomerId }).IsUnique();
        modelBuilder.Entity<Conversation>().HasIndex(x => new { x.TenantId, x.ChannelId, x.ExternalConversationId }).IsUnique();
        modelBuilder.Entity<Conversation>().Property(x => x.Version).IsRowVersion();
        modelBuilder.Entity<Message>().HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
        modelBuilder.Entity<Message>().HasIndex(x => new { x.TenantId, x.ChannelId, x.ExternalMessageId }).IsUnique().HasFilter("\"ExternalMessageId\" IS NOT NULL");
        modelBuilder.Entity<Message>().HasIndex(x => new { x.TenantId, x.ConversationId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        modelBuilder.Entity<InternalNote>().HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<Invitation>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<CannedResponseEntity>().HasIndex(x => new { x.TenantId, x.Shortcut }).IsUnique();
        modelBuilder.Entity<global::UnifiedInbox.Domain.WebhookReceipt>().HasIndex(x => new { x.ChannelId, x.ProviderEventId }).IsUnique();
        modelBuilder.Entity<OutboxEvent>().HasIndex(x => new { x.Status, x.AvailableAt });
    }

    private void ConfigureTenant<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasKey("Id");
        modelBuilder.Entity<TEntity>().HasQueryFilter("TenantFilter", entity => CurrentTenantId == null || entity.TenantId == CurrentTenantId);
        modelBuilder.Entity<TEntity>().HasIndex("TenantId");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentTenantId is { } tenantId)
        {
            foreach (var entry in ChangeTracker.Entries<ITenantScoped>().Where(x => x.State == EntityState.Added))
                if (entry.Entity.TenantId != tenantId) throw new InvalidOperationException("Cross-tenant writes are not allowed.");
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
