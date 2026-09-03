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
    public DbSet<VerificationToken> VerificationTokens => Set<VerificationToken>();
    public DbSet<ChannelHealth> ChannelHealth => Set<ChannelHealth>();
    public DbSet<global::UnifiedInbox.Domain.WebhookReceipt> WebhookReceipts => Set<global::UnifiedInbox.Domain.WebhookReceipt>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    /// <summary>Unscoped: webhook routing only. Never expose via tenant queries.</summary>
    public DbSet<ProviderRoute> ProviderRoutes => Set<ProviderRoute>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>().HasKey(x => x.Id);
        modelBuilder.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        ConfigureTenant<User>(modelBuilder); ConfigureTenant<Channel>(modelBuilder); ConfigureTenant<ChannelCredential>(modelBuilder);
        ConfigureTenant<Contact>(modelBuilder); ConfigureTenant<Conversation>(modelBuilder); ConfigureTenant<Message>(modelBuilder);
        ConfigureTenant<InternalNote>(modelBuilder); ConfigureTenant<RefreshToken>(modelBuilder); ConfigureTenant<Invitation>(modelBuilder);
        ConfigureTenant<Attachment>(modelBuilder); ConfigureTenant<CannedResponseEntity>(modelBuilder); ConfigureTenant<NotificationEntity>(modelBuilder);
        ConfigureTenant<AuditEntryEntity>(modelBuilder); ConfigureTenant<global::UnifiedInbox.Domain.WebhookReceipt>(modelBuilder); ConfigureTenant<OutboxEvent>(modelBuilder);
        ConfigureTenant<VerificationToken>(modelBuilder); ConfigureTenant<ChannelHealth>(modelBuilder);
        // ProviderRoute is deliberately unscoped: webhooks must resolve tenant from
        // provider asset id before any tenant context exists.
        modelBuilder.Entity<ProviderRoute>().HasKey(x => x.Id);
        modelBuilder.Entity<ProviderRoute>().HasIndex(x => new { x.Provider, x.ProviderAssetId }).IsUnique();
        // Explicit tenant-aware relations
        modelBuilder.Entity<Conversation>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.ChannelId }).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Message>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<InternalNote>().HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).HasPrincipalKey(x => x.Id).OnDelete(DeleteBehavior.Cascade);
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
        modelBuilder.Entity<VerificationToken>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(x => x.FamilyId);
        modelBuilder.Entity<global::UnifiedInbox.Domain.WebhookReceipt>().HasIndex(x => new { x.ChannelId, x.ProviderEventId }).IsUnique();
        modelBuilder.Entity<OutboxEvent>().HasIndex(x => new { x.Status, x.AvailableAt });
    }

    private void ConfigureTenant<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasKey("Id");
        // Fail closed: with no tenant in context, scoped queries return nothing.
        // Privileged code (login, webhook routing, worker) must opt out explicitly
        // via IgnoreQueryFilters for a single query.
        modelBuilder.Entity<TEntity>().HasQueryFilter("TenantFilter", entity => CurrentTenantId != null && entity.TenantId == CurrentTenantId);
        modelBuilder.Entity<TEntity>().HasIndex("TenantId");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Cross-tenant writes are rejected when a tenant context is established.
        // Anonymous bootstrap (registration) and worker paths set TenantId explicitly
        // on new entities; reads remain fail-closed via query filters + FORCE RLS.
        if (CurrentTenantId is { } tenantId)
        {
            foreach (var entry in ChangeTracker.Entries<ITenantScoped>().Where(x => x.State == EntityState.Added))
                if (entry.Entity.TenantId != tenantId) throw new InvalidOperationException("Cross-tenant writes are not allowed.");
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
