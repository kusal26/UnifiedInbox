using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application.Tenancy;

namespace UnifiedInbox.Infrastructure.Persistence;

public sealed class TenantExecutionScope(InboxDbContext db) : ITenantExecutionScope
{
    private static readonly AsyncLocal<Guid?> AmbientTenant = new();

    public Guid? CurrentTenantId => AmbientTenant.Value;
    internal static Guid? CurrentAmbientTenantId => AmbientTenant.Value;

    public Task RunAsync(Guid tenantId, Func<CancellationToken, Task> action, CancellationToken token) =>
        RunAsync<object?>(tenantId, async innerToken => { await action(innerToken); return null; }, token);

    public async Task<T> RunAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        if (CurrentTenantId is { } current)
        {
            if (current != tenantId) throw new InvalidOperationException("A tenant execution scope cannot switch tenants while nested.");
            return await action(token);
        }

        AmbientTenant.Value = tenantId;
        try
        {
            if (!db.Database.IsRelational()) return await action(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"select set_config('app.current_tenant', {tenantId.ToString()}, true)", token);
            var result = await action(token);
            await transaction.CommitAsync(token);
            return result;
        }
        finally
        {
            AmbientTenant.Value = null;
        }
    }
}
