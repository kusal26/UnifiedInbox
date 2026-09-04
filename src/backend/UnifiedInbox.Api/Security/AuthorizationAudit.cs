using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Security;

/// <summary>
/// Records authorization denials (policy failures and membership-guard denials) into the audit log.
/// Writes carry tenant, actor, action, normalized route, HTTP method, and policy — never request
/// bodies or secrets. Best-effort by design: auditing must not change the authorization outcome.
/// </summary>
internal static class AuthorizationAudit
{
    public const string DeniedAction = "authorization.denied";

    public static bool TryActor(HttpContext context, out Guid tenantId, out Guid userId)
    {
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        return Guid.TryParse(context.User.FindFirstValue("tenant_id"), out tenantId)
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }

    /// <summary>Normalized route pattern (e.g. <c>/api/v1/channels/{id:guid}/test</c>) rather than a
    /// concrete path, so resource ids are not treated as audit data.</summary>
    public static string Route(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is RouteEndpoint route) return "/" + route.RoutePattern.RawText;
        return context.Request.Path.Value ?? "";
    }

    /// <summary>The named authorization policy for the request. Resolved while the endpoint is known
    /// and cached on the request so a later (post-rollback) audit can report it even after the
    /// exception pipeline clears the endpoint feature.</summary>
    public static string PolicyName(HttpContext context)
    {
        const string key = "unified-inbox.authorization.policy";
        if (context.Items.TryGetValue(key, out var cached) && cached is string existing && existing.Length > 0) return existing;
        var policy = "authenticated";
        var endpoint = context.GetEndpoint();
        if (endpoint is not null)
        {
            var attributes = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            for (var i = attributes.Count - 1; i >= 0; i--)
                if (!string.IsNullOrWhiteSpace(attributes[i].Policy)) { policy = attributes[i].Policy!; break; }
        }
        context.Items[key] = policy;
        return policy;
    }

    /// <summary>Records a policy denial. Runs while the tenant execution scope is still active, so it
    /// writes through the request-scoped context and the scope transaction commits it.</summary>
    public static async Task RecordInRequestScopeAsync(HttpContext context, Guid tenantId, Guid userId, string route, string method, string policy)
    {
        try
        {
            var db = context.RequestServices.GetRequiredService<InboxDbContext>();
            db.AuditEntries.Add(Entry(tenantId, userId, route, method, policy));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Never let an audit failure change the authorization decision.
        }
    }

    /// <summary>Records a membership-guard denial after the request transaction was rolled back. A
    /// fresh scope and explicit transaction set <c>app.current_tenant</c> so the write commits.</summary>
    public static async Task RecordAfterRollbackAsync(IServiceProvider services, Guid tenantId, Guid userId, string route, string method, string policy)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InboxDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await db.Database.ExecuteSqlInterpolatedAsync($"select set_config('app.current_tenant', {tenantId.ToString()}, true)", CancellationToken.None);
            db.AuditEntries.Add(Entry(tenantId, userId, route, method, policy));
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            // Never let an audit failure change the authorization decision.
        }
    }

    private static AuditEntryEntity Entry(Guid tenantId, Guid userId, string route, string method, string policy) =>
        new()
        {
            TenantId = tenantId,
            ActorId = userId,
            Action = DeniedAction,
            Resource = route,
            Metadata = JsonSerializer.Serialize(new { method, policy }),
        };
}
