namespace UnifiedInbox.Domain;

public sealed class CannedResponseEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Title { get; set; } = "";
    public string Shortcut { get; set; } = ""; public string Content { get; set; } = "";
}

public sealed class NotificationEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Type { get; set; } = "";
    public string Text { get; set; } = ""; public bool IsRead { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NotificationPreference : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UserId { get; set; }
    public string Kind { get; set; } = ""; public bool Enabled { get; set; } = true;
}

public sealed class AuditEntryEntity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid? ActorId { get; set; }
    public string Action { get; set; } = ""; public string Resource { get; set; } = ""; public string Metadata { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
