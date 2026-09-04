using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

public sealed class InvitationService(InboxDbContext db, ICurrentTenant current, IPasswordHasher<User> passwords, IMailSender mail) : IInvitationService
{
    public async Task<IReadOnlyList<InvitationSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        return await db.Invitations
            .Where(x => x.AcceptedAt == null && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new InvitationSummary(x.Id, x.Email, x.Role, x.ExpiresAt, x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<InvitationSummary> InviteAsync(string email, UserRole role, CancellationToken cancellationToken)
    {
        var inviter = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        if (inviter.Role == UserRole.Admin && role == UserRole.Owner)
            throw new InboxException("invitation_forbidden", "Administrators cannot invite owners.", 403);
        var normalized = email.Trim();
        if (!normalized.Contains('@') || normalized.Length > 256) throw new ArgumentException("A valid email address is required.", nameof(email));
        var tenantId = inviter.TenantId;
        var normalizedEmail = normalized.ToUpperInvariant();
        if (await db.Users.AnyAsync(x => x.TenantId == tenantId && x.NormalizedEmail == normalizedEmail && x.IsActive, cancellationToken))
            throw new InboxException("already_member", "This email already belongs to an active workspace member.", 409);
        var pending = await db.Invitations.Where(x => x.TenantId == tenantId && x.Email == normalized && x.AcceptedAt == null && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow).ToListAsync(cancellationToken);
        foreach (var old in pending) old.RevokedAt = DateTimeOffset.UtcNow;

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var invitation = new Invitation
        {
            TenantId = tenantId,
            Email = normalized,
            Role = role,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(72),
            InvitedById = inviter.Id,
        };
        db.Invitations.Add(invitation);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = tenantId, ActorId = inviter.Id, Action = "invitation.created", Resource = invitation.Id.ToString(), Metadata = $"{{\"email\":\"{normalized}\",\"role\":\"{role}\"}}" });
        await db.SaveChangesAsync(cancellationToken);
        await mail.SendAsync(normalized, "You are invited to join the workspace", $"Accept within 72 hours with this token: {raw}", cancellationToken);
        return new(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, invitation.CreatedAt);
    }

    public async Task<bool> AcceptAsync(string token, string displayName, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
        if (password.Length < 12) throw new ArgumentException("Password must contain at least 12 characters.", nameof(password));
        var hash = Hash(token.Trim());
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (invitation is null || invitation.RevokedAt is not null || invitation.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        if (invitation.AcceptedAt is not null) throw new InboxException("already_member", "This invitation was already accepted.", 409);
        var normalizedEmail = invitation.Email.Trim().ToUpperInvariant();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(x => x.TenantId == invitation.TenantId && x.NormalizedEmail == normalizedEmail && x.IsActive, cancellationToken))
            throw new InboxException("already_member", "This invitation was already accepted.", 409);
        var user = new User(Guid.NewGuid(), invitation.TenantId, invitation.Email.Trim(), displayName.Trim(), invitation.Role)
        {
            NormalizedEmail = normalizedEmail,
            EmailVerifiedAt = DateTimeOffset.UtcNow, // invitation proves mailbox ownership
        };
        user.PasswordHash = passwords.HashPassword(user, password);
        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        db.Users.Add(user);
        db.AuditEntries.Add(new AuditEntryEntity { TenantId = invitation.TenantId, ActorId = user.Id, Action = "invitation.accepted", Resource = invitation.Id.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await MembershipGuard.RequireRoleAsync(db, current, UserRole.Admin, cancellationToken);
        var invitation = await db.Invitations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (invitation is null) return false;
        if (invitation.AcceptedAt is null && invitation.RevokedAt is null)
        {
            invitation.RevokedAt = DateTimeOffset.UtcNow;
            db.AuditEntries.Add(new AuditEntryEntity { TenantId = actor.TenantId, ActorId = actor.Id, Action = "invitation.revoked", Resource = invitation.Id.ToString() });
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
