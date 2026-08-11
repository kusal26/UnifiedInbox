using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Infrastructure.Persistence;

public sealed class InboxDbContext(DbContextOptions<InboxDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<InternalNote> InternalNotes => Set<InternalNote>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>().HasKey(x => x.Id);
        modelBuilder.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<User>().HasKey(x => x.Id);
        modelBuilder.Entity<Channel>().HasKey(x => x.Id);
        modelBuilder.Entity<Contact>().HasKey(x => x.Id);
        modelBuilder.Entity<Contact>().HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId, x.ExternalCustomerId }).IsUnique();
        modelBuilder.Entity<Conversation>().HasKey(x => x.Id);
        modelBuilder.Entity<Message>().HasKey(x => x.Id);
        modelBuilder.Entity<Message>().HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
        modelBuilder.Entity<Message>().HasIndex(x => new { x.TenantId, x.ExternalMessageId }).IsUnique().HasFilter("\"ExternalMessageId\" IS NOT NULL");
        modelBuilder.Entity<InternalNote>().HasKey(x => x.Id);
        modelBuilder.Entity<InternalNote>().HasIndex(x => new { x.TenantId, x.ConversationId, x.Sequence }).IsUnique();
    }
}
