namespace UnifiedInbox.Domain;

public interface ITenantScoped { Guid TenantId { get; } }

public sealed class Tenant
{
    private Tenant() { }
    public Tenant(Guid id, string slug, string name) { Id = id; Slug = slug; Name = name; }
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public int RetentionDays { get; set; } = 365;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum UserRole { Owner, Admin, Agent }

public sealed class User : ITenantScoped
{
    private User() { }
    public User(Guid id, Guid tenantId, string email, string displayName, UserRole role) { Id = id; TenantId = tenantId; Email = email; DisplayName = displayName; Role = role; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RefreshToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UserId { get; set; }
    public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; } public Guid? ReplacedById { get; set; }
    /// <summary>Token family for reuse detection. All rotations of one session share a family.</summary>
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum VerificationPurpose { EmailVerification, PasswordReset }

public sealed class VerificationToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public Guid UserId { get; set; }
    public string TokenHash { get; set; } = ""; public VerificationPurpose Purpose { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Invitation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } public string Email { get; set; } = "";
    public UserRole Role { get; set; } public string TokenHash { get; set; } = ""; public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? InvitedById { get; set; }
}
