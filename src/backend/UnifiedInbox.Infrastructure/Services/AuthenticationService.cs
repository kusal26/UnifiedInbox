using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Application.Tenancy;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AuthenticationService(InboxDbContext db, IPasswordHasher<User> passwords, ITokenIssuer tokens, ICurrentTenant currentTenant, IMailSender mail, ITenantExecutionScope executionScope) : IAuthService
{
    private ITenantExecutionScope Scope { get; } = executionScope;

    public async Task<AuthTokens?> LoginAsync(string tenantSlug, string email, string password, CancellationToken token)
    {
        var tenantId = await db.Tenants.Where(x => x.Slug == tenantSlug.Trim().ToLowerInvariant()).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (tenantId is null) return null;
        return await Scope.RunAsync(tenantId.Value, async scopedToken =>
        {
            var normalized = email.Trim().ToUpperInvariant();
            var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.IsActive, scopedToken);
            if (user is null || user.EmailVerifiedAt is null) return null;
            if (passwords.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            {
                Audit(user, "auth.login.failed", user.Id);
                await db.SaveChangesAsync(scopedToken);
                return null;
            }
            Audit(user, "auth.login.succeeded", user.Id);
            await db.SaveChangesAsync(scopedToken);
            return await CreateTokensAsync(user, scopedToken);
        }, token);
    }

    public async Task RegisterAsync(Registration registration, CancellationToken token)
    {
        var slug = registration.WorkspaceSlug.Trim().ToLowerInvariant();
        if (slug.Length is < 3 or > 64 || !slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) throw new ArgumentException("Workspace slug must contain 3-64 letters, numbers, or hyphens.");
        if (registration.Password.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.");
        if (await db.Tenants.AnyAsync(x => x.Slug == slug, token)) throw new InvalidOperationException("Workspace slug is already in use.");
        var tenant = new Tenant(Guid.NewGuid(), slug, registration.WorkspaceName.Trim());
        var user = new User(Guid.NewGuid(), tenant.Id, registration.Email.Trim(), registration.DisplayName.Trim(), UserRole.Owner) { NormalizedEmail = registration.Email.Trim().ToUpperInvariant() };
        user.PasswordHash = passwords.HashPassword(user, registration.Password);
        var raw = TenantToken.Create(tenant.Id);
        await Scope.RunAsync(tenant.Id, async scopedToken =>
        {
            db.Tenants.Add(tenant); db.Users.Add(user);
            db.VerificationTokens.Add(new VerificationToken { TenantId = tenant.Id, UserId = user.Id, TokenHash = Hash(raw), Purpose = VerificationPurpose.EmailVerification, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            Audit(user, "tenant.registered", tenant.Id);
            await db.SaveChangesAsync(scopedToken);
        }, token);
        await mail.SendAsync(user.Email, "Verify your workspace email", $"Verify within 1 hour with this token: {raw}", token);
    }

    public Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken) => WithTokenTenant(token, false, async scopedToken =>
    {
        var stored = await db.VerificationTokens.SingleOrDefaultAsync(x => x.TokenHash == Hash(token) && x.Purpose == VerificationPurpose.EmailVerification, scopedToken);
        if (stored is null || stored.ConsumedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == stored.UserId, scopedToken);
        if (user is null) return false;
        stored.ConsumedAt = DateTimeOffset.UtcNow; user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        Audit(user, "auth.email.verified", user.Id);
        await db.SaveChangesAsync(scopedToken); return true;
    }, cancellationToken);

    public Task<bool> ResendVerificationAsync(string email, CancellationToken token) => FindByEmailAsync(email, async (user, scopedToken) =>
    {
        if (user.EmailVerifiedAt is not null) return true;
        foreach (var item in await db.VerificationTokens.Where(x => x.UserId == user.Id && x.Purpose == VerificationPurpose.EmailVerification && x.ConsumedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(scopedToken)) item.ConsumedAt = DateTimeOffset.UtcNow;
        var raw = TenantToken.Create(user.TenantId);
        db.VerificationTokens.Add(new VerificationToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = Hash(raw), Purpose = VerificationPurpose.EmailVerification, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync(scopedToken);
        await mail.SendAsync(user.Email, "Verify your workspace email", $"Verify within 1 hour with this token: {raw}", scopedToken);
        return true;
    }, token);

    public Task<bool> ForgotPasswordAsync(string email, CancellationToken token) => FindByEmailAsync(email, async (user, scopedToken) =>
    {
        if (user.EmailVerifiedAt is null) return true;
        var raw = TenantToken.Create(user.TenantId);
        db.VerificationTokens.Add(new VerificationToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = Hash(raw), Purpose = VerificationPurpose.PasswordReset, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        Audit(user, "auth.password.reset.requested", user.Id);
        await db.SaveChangesAsync(scopedToken);
        await mail.SendAsync(user.Email, "Reset your password", $"Reset within 1 hour with this token: {raw}", scopedToken);
        return true;
    }, token);

    public Task<bool> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        if (newPassword.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.");
        return WithTokenTenant(token, false, async scopedToken =>
        {
            var stored = await db.VerificationTokens.SingleOrDefaultAsync(x => x.TokenHash == Hash(token) && x.Purpose == VerificationPurpose.PasswordReset, scopedToken);
            if (stored is null || stored.ConsumedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == stored.UserId && x.IsActive, scopedToken);
            if (user is null) return false;
            stored.ConsumedAt = DateTimeOffset.UtcNow; user.PasswordHash = passwords.HashPassword(user, newPassword);
            foreach (var session in await db.RefreshTokens.Where(x => x.UserId == user.Id && x.RevokedAt == null).ToListAsync(scopedToken)) session.RevokedAt = DateTimeOffset.UtcNow;
            Audit(user, "auth.password.reset.completed", user.Id);
            await db.SaveChangesAsync(scopedToken); return true;
        }, cancellationToken);
    }

    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken token)
    {
        if (!TenantToken.TryGetTenantId(refreshToken, out var tenantId)) return null;

        // Reuse detection revokes the whole family and must be durable before the caller
        // learns about it: the detection scope commits normally, then we throw outside it.
        var reused = await Scope.RunAsync(tenantId, async scopedToken =>
        {
            var stored = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == Hash(refreshToken), scopedToken);
            if (stored is null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            if (stored.RevokedAt is not null)
            {
                foreach (var member in await db.RefreshTokens.Where(x => x.FamilyId == stored.FamilyId && x.RevokedAt == null).ToListAsync(scopedToken)) member.RevokedAt = DateTimeOffset.UtcNow;
                db.AuditEntries.Add(new AuditEntryEntity { TenantId = stored.TenantId, ActorId = stored.UserId, Action = "auth.token.reuse.detected", Resource = stored.UserId.ToString() });
                await db.SaveChangesAsync(scopedToken);
                return true;
            }
            return false;
        }, token);
        if (reused) throw new InboxException("token_reuse_detected", "Refresh token reuse detected. All sessions were revoked.", 401);

        return await Scope.RunAsync(tenantId, async scopedToken =>
        {
            var stored = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == Hash(refreshToken), scopedToken);
            if (stored is null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return null;
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == stored.UserId && x.IsActive, scopedToken);
            if (user is null || user.EmailVerifiedAt is null) return null;
            stored.RevokedAt = DateTimeOffset.UtcNow;
            var result = await CreateTokensAsync(user, scopedToken, stored.FamilyId, false);
            stored.ReplacedById = db.RefreshTokens.Local.Single(x => x.TokenHash == Hash(result.RefreshToken)).Id;
            await db.SaveChangesAsync(scopedToken); return result;
        }, token);
    }

    public Task RevokeAsync(string refreshToken, CancellationToken token) => WithTokenTenant(refreshToken, async scopedToken =>
    {
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == Hash(refreshToken), scopedToken);
        if (stored is null) return;
        stored.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = stored.TenantId, ActorId = stored.UserId, Action = "auth.session.revoked", Resource = stored.Id.ToString() });
        await db.SaveChangesAsync(scopedToken);
    }, token);

    public async Task<IReadOnlyList<SessionInfo>> SessionsAsync(CancellationToken token)
    {
        if (currentTenant.UserId is not { } userId) throw new UnauthorizedAccessException();
        return await db.RefreshTokens.Where(x => x.UserId == userId && x.ExpiresAt > DateTimeOffset.UtcNow && x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).Select(x => new SessionInfo(x.Id, x.CreatedAt, x.ExpiresAt, false)).ToListAsync(token);
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken token)
    {
        if (currentTenant.UserId is not { } userId) throw new UnauthorizedAccessException();
        var session = await db.RefreshTokens.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, token); if (session is null) return;
        session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = session.TenantId, ActorId = userId, Action = "auth.session.revoked", Resource = session.Id.ToString() });
        await db.SaveChangesAsync(token);
    }

    public async Task RevokeAllSessionsAsync(CancellationToken token)
    {
        if (currentTenant.UserId is not { } userId || currentTenant.TenantId is not { } tenantId) throw new UnauthorizedAccessException();
        foreach (var session in await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(token)) session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = userId, Action = "auth.sessions.revoked.all", Resource = userId.ToString() });
        await db.SaveChangesAsync(token);
    }

    public async Task<CurrentUser?> MeAsync(CancellationToken token)
    {
        if (currentTenant.UserId is not { } userId || currentTenant.TenantId is not { } tenantId) return null;
        return await db.Users.Where(x => x.Id == userId).Join(db.Tenants, user => user.TenantId, tenant => tenant.Id, (user, tenant) => new CurrentUser(user.Id, tenantId, user.Email, user.DisplayName, user.Role, tenant.Name)).SingleOrDefaultAsync(token);
    }

    private async Task<AuthTokens> CreateTokensAsync(User user, CancellationToken token, Guid? familyId = null, bool save = true)
    {
        var (accessToken, expiresAt) = tokens.Issue(user);
        var refreshToken = TenantToken.Create(user.TenantId, 48);
        db.RefreshTokens.Add(new RefreshToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = Hash(refreshToken), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), FamilyId = familyId ?? Guid.NewGuid() });
        if (save) await db.SaveChangesAsync(token);
        return new(accessToken, refreshToken, expiresAt);
    }

    private async Task<bool> FindByEmailAsync(string email, Func<User, CancellationToken, Task<bool>> action, CancellationToken token)
    {
        var normalized = email.Trim().ToUpperInvariant();
        foreach (var tenantId in await db.Tenants.AsNoTracking().Select(x => x.Id).ToListAsync(token))
        {
            var found = await Scope.RunAsync(tenantId, async scopedToken =>
            {
                var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.IsActive, scopedToken);
                return user is not null && await action(user, scopedToken);
            }, token);
            if (found) return true;
        }
        return true;
    }

    private Task<T> WithTokenTenant<T>(string token, T invalid, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) => TenantToken.TryGetTenantId(token, out var tenantId) ? Scope.RunAsync(tenantId, action, cancellationToken) : Task.FromResult(invalid);
    private Task WithTokenTenant(string token, Func<CancellationToken, Task> action, CancellationToken cancellationToken) => TenantToken.TryGetTenantId(token, out var tenantId) ? Scope.RunAsync(tenantId, action, cancellationToken) : Task.CompletedTask;
    private void Audit(User user, string action, Guid resource) => db.AuditEntries.Add(new AuditEntryEntity { TenantId = user.TenantId, ActorId = user.Id, Action = action, Resource = resource.ToString() });
    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
