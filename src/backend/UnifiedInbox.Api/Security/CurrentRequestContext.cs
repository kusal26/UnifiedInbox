using System.Security.Claims;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Api.Security;

public sealed class CurrentRequestContext(IHttpContextAccessor accessor) : ICurrentTenant
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;
    // The claim is routing input only. Program enters ITenantExecutionScope before
    // authorized endpoints touch tenant tables, where PostgreSQL enforces the value.
    public Guid? TenantId => Guid.TryParse(User?.FindFirstValue("tenant_id"), out var value) && value != Guid.Empty ? value : null;
    public Guid? UserId => Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var value) ? value : null;
    public UserRole? Role => Enum.TryParse<UserRole>(User?.FindFirstValue(ClaimTypes.Role), out var value) ? value : null;
}
