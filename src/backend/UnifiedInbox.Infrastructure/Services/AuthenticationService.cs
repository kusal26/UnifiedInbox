using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class AuthenticationService(InboxDbContext db, IPasswordHasher<User> passwords, ITokenIssuer tokens, ICurrentTenant currentTenant) : IAuthService
{
    public async Task<AuthTokens?> LoginAsync(string tenantSlug, string email, string password, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(x => x.Slug == tenantSlug.ToLower(), cancellationToken);
        if (tenant is null) return null;
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenant.Id && x.NormalizedEmail == normalized && x.IsActive, cancellationToken);
        if (user is null || passwords.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed) return null;
        return await CreateTokensAsync(user, cancellationToken);
    }

    public async Task<AuthTokens> RegisterAsync(Registration registration, CancellationToken cancellationToken)
    {
        var slug = registration.WorkspaceSlug.Trim().ToLowerInvariant();
        if (slug.Length is < 3 or > 64 || !slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')) throw new ArgumentException("Workspace slug must contain 3-64 letters, numbers, or hyphens.");
        if (registration.Password.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.");
        if (await db.Tenants.AnyAsync(x => x.Slug == slug, cancellationToken)) throw new InvalidOperationException("Workspace slug is already in use.");
        var tenant = new Tenant(Guid.NewGuid(), slug, registration.WorkspaceName.Trim());
        var user = new User(Guid.NewGuid(), tenant.Id, registration.Email.Trim(), registration.DisplayName.Trim(), UserRole.Owner) { NormalizedEmail = registration.Email.Trim().ToUpperInvariant(), EmailVerifiedAt = null };
        user.PasswordHash = passwords.HashPassword(user, registration.Password);
        db.Tenants.Add(tenant); db.Users.Add(user);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenant.Id, ActorId = user.Id, Action = "tenant.registered", Resource = tenant.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await CreateTokensAsync(user, cancellationToken);
    }

    public async Task<AuthTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == stored.UserId && x.TenantId == stored.TenantId && x.IsActive, cancellationToken);
        if (user is null) return null;
        stored.RevokedAt = DateTimeOffset.UtcNow;
        var result = await CreateTokensAsync(user, cancellationToken, save: false);
        stored.ReplacedById = db.RefreshTokens.Local.Single(x => x.TokenHash == Hash(result.RefreshToken)).Id;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var stored = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == Hash(refreshToken), cancellationToken);
        if (stored is null) return;
        stored.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CurrentUser?> MeAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.UserId is not { } userId || currentTenant.TenantId is not { } tenantId) return null;
        return await db.Users.Where(x => x.Id == userId).Join(db.Tenants, user => user.TenantId, tenant => tenant.Id,
            (user, tenant) => new CurrentUser(user.Id, tenantId, user.Email, user.DisplayName, user.Role, tenant.Name)).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<AuthTokens> CreateTokensAsync(User user, CancellationToken cancellationToken, bool save = true)
    {
        var (accessToken, expiresAt) = tokens.Issue(user);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.RefreshTokens.Add(new RefreshToken { TenantId = user.TenantId, UserId = user.Id, TokenHash = Hash(refreshToken), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        if (save) await db.SaveChangesAsync(cancellationToken);
        return new(accessToken, refreshToken, expiresAt);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
