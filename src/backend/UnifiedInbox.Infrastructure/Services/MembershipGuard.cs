using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Infrastructure.Services;

/// <summary>
/// Re-reads membership from the database instead of trusting JWT claims, so role
/// changes and deactivations take effect immediately on sensitive operations.
/// </summary>
internal static class MembershipGuard
{
    public static async Task<User> RequireRoleAsync(InboxDbContext db, ICurrentTenant current, UserRole minimum, CancellationToken token)
    {
        if (current.TenantId is not { } || current.UserId is not { } userId) throw new UnauthorizedAccessException();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, token);
        if (user is null || !user.IsActive || user.EmailVerifiedAt is null) throw new UnauthorizedAccessException();
        var authorized = minimum switch
        {
            UserRole.Owner => user.Role == UserRole.Owner,
            UserRole.Admin => user.Role is UserRole.Owner or UserRole.Admin,
            _ => true,
        };
        if (!authorized) throw new UnauthorizedAccessException();
        return user;
    }
}
