using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AuthenticationService(InboxDbContext db, IPasswordHasher<User> passwords, ITokenIssuer tokens, ICurrentTenant currentTenant, IMailSender mail) : IAuthService
{
    public async Task<AuthTokens?> LoginAsync(string tenantSlug, string email, string password, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Slug == tenantSlug.ToLower(), cancellationToken);
        if (tenant is null) return null;
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.NormalizedEmail == normalized && x.IsActive, cancellationToken);
        if (user is null || user.EmailVerifiedAt is null) return null;
        if (passwords.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenant.Id, ActorId = user.Id, Action = "auth.login.failed", Resource = user.Id.ToString() });
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenant.Id, ActorId = user.Id, Action = "auth.login.succeeded", Resource = user.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await CreateTokensAsync(user, cancellationToken);
    }

    public async Task RegisterAsync(Registration registration, CancellationToken cancellationToken)
    {
        var slug = registration.WorkspaceSlug.Trim().ToLowerInvariant();
        if (slug.Length is < 3 or > 64 || !slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) throw new ArgumentException("Workspace slug must contain 3-64 letters, numbers, or hyphens.");
        if (registration.Password.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.");
        if (await db.Tenants.AnyAsync(x => x.Slug == slug, cancellationToken)) throw new InvalidOperationException("Workspace slug is already in use.");
        var tenant = new Tenant(Guid.NewGuid(), slug, registration.WorkspaceName.Trim());
        var user = new User(Guid.NewGuid(), tenant.Id, registration.Email.Trim(), registration.DisplayName.Trim(), UserRole.Owner) { NormalizedEmail = registration.Email.Trim().ToUpperInvariant(), EmailVerifiedAt = null };
        user.PasswordHash = passwords.HashPassword(user, registration.Password);
        db.Tenants.Add(tenant); db.Users.Add(user);
        var (rawToken, hash) = NewToken();
        db.VerificationTokens.Add(new VerificationToken { TenantId = tenant.Id, UserId = user.Id, TokenHash = hash, Purpose = VerificationPurpose.EmailVerification, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenant.Id, ActorId = user.Id, Action = "tenant.registered", Resource = tenant.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await mail.SendAsync(user.Email, "Verify your workspace email", $"Verify within 1 hour with this token: {rawToken}", cancellationToken);
    }

    public async Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var stored = await db.VerificationTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == VerificationPurpose.EmailVerification, cancellationToken);
        if (stored is null || stored.ConsumedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == stored.UserId && x.TenantId == stored.TenantId, cancellationToken);
        if (user is null) return false;
        stored.ConsumedAt = DateTimeOffset.UtcNow;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = user.TenantId, ActorId = user.Id, Action = "auth.email.verified", Resource = user.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResendVerificationAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.IsActive, cancellationToken);
        if (user is null || user.EmailVerifiedAt is not null) return true; // avoid account enumeration
        var existing = await db.VerificationTokens.IgnoreQueryFilters().Where(x => x.UserId == user.Id && x.Purpose == VerificationPurpose.EmailVerification && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(cancellationToken);
        foreach (var item in existing) item.ConsumedAt = DateTimeOffset.UtcNow;
        var (rawToken, hash) = NewToken();
        db.VerificationTokens.Add(new VerificationToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = hash, Purpose = VerificationPurpose.EmailVerification, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync(cancellationToken);
        await mail.SendAsync(user.Email, "Verify your workspace email", $"Verify within 1 hour with this token: {rawToken}", cancellationToken);
        return true;
    }

    public async Task<bool> ForgotPasswordAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.IsActive, cancellationToken);
        if (user is null || user.EmailVerifiedAt is null) return true; // avoid account enumeration
        var (rawToken, hash) = NewToken();
        db.VerificationTokens.Add(new VerificationToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = hash, Purpose = VerificationPurpose.PasswordReset, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = user.TenantId, ActorId = user.Id, Action = "auth.password.reset.requested", Resource = user.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await mail.SendAsync(user.Email, "Reset your password", $"Reset within 1 hour with this token: {rawToken}", cancellationToken);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        if (newPassword.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.");
        var hash = Hash(token);
        var stored = await db.VerificationTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == VerificationPurpose.PasswordReset, cancellationToken);
        if (stored is null || stored.ConsumedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == stored.UserId && x.TenantId == stored.TenantId && x.IsActive, cancellationToken);
        if (user is null) return false;
        stored.ConsumedAt = DateTimeOffset.UtcNow;
        user.PasswordHash = passwords.HashPassword(user, newPassword);
        // Reset invalidates every session: force re-login on all devices.
        var sessions = await db.RefreshTokens.IgnoreQueryFilters().Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = user.TenantId, ActorId = user.Id, Action = "auth.password.reset.completed", Resource = user.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (stored is null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        if (stored.RevokedAt is not null)
        {
            // Token reuse: a rotated token was replayed. Revoke the whole family.
            var family = await db.RefreshTokens.IgnoreQueryFilters().Where(x => x.FamilyId == stored.FamilyId && x.RevokedAt == null).ToListAsync(cancellationToken);
            foreach (var member in family) member.RevokedAt = DateTimeOffset.UtcNow;
            db.AuditEntries.Add(new AuditEntryEntity { TenantId = stored.TenantId, ActorId = stored.UserId, Action = "auth.token.reuse.detected", Resource = stored.UserId.ToString() });
            await db.SaveChangesAsync(cancellationToken);
            throw new InboxException("token_reuse_detected", "Refresh token reuse detected. All sessions were revoked.", 401);
        }
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == stored.UserId && x.TenantId == stored.TenantId && x.IsActive, cancellationToken);
        if (user is null || user.EmailVerifiedAt is null) return null;
        stored.RevokedAt = DateTimeOffset.UtcNow;
        var result = await CreateTokensAsync(user, cancellationToken, familyId: stored.FamilyId, save: false);
        stored.ReplacedById = db.RefreshTokens.Local.Single(x => x.TokenHash == Hash(result.RefreshToken)).Id;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var stored = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == Hash(refreshToken), cancellationToken);
        if (stored is null) return;
        stored.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = stored.TenantId, ActorId = stored.UserId, Action = "auth.session.revoked", Resource = stored.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SessionInfo>> SessionsAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.UserId is not { } userId) throw new UnauthorizedAccessException();
        return await db.RefreshTokens.Where(x => x.UserId == userId && x.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new SessionInfo(x.Id, x.CreatedAt, x.ExpiresAt, false))
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (currentTenant.UserId is not { } userId) throw new UnauthorizedAccessException();
        var session = await db.RefreshTokens.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, cancellationToken);
        if (session is null) return;
        session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = session.TenantId, ActorId = userId, Action = "auth.session.revoked", Resource = session.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllSessionsAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.UserId is not { } userId || currentTenant.TenantId is not { } tenantId) throw new UnauthorizedAccessException();
        var sessions = await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "auth.sessions.revoked.all", Resource = userId.ToString() });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CurrentUser?> MeAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.UserId is not { } userId || currentTenant.TenantId is not { } tenantId) return null;
        return await db.Users.Where(x => x.Id == userId).Join(db.Tenants, user => user.TenantId, tenant => tenant.Id,
            (user, tenant) => new CurrentUser(user.Id, tenantId, user.Email, user.DisplayName, user.Role, tenant.Name)).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<AuthTokens> CreateTokensAsync(User user, CancellationToken cancellationToken, Guid? familyId = null, bool save = true)
    {
        var (accessToken, expiresAt) = tokens.Issue(user);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.RefreshTokens.Add(new RefreshToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = Hash(refreshToken), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), FamilyId = familyId ?? Guid.NewGuid() });
        if (save) await db.SaveChangesAsync(cancellationToken);
        return new(accessToken, refreshToken, expiresAt);
    }

    private static (string Raw, string Hash) NewToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
