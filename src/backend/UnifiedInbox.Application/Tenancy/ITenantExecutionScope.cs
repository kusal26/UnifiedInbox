namespace UnifiedInbox.Application.Tenancy;

public interface ITenantExecutionScope
{
    Guid? CurrentTenantId { get; }
    Task RunAsync(Guid tenantId, Func<CancellationToken, Task> action, CancellationToken token);
    Task<T> RunAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> action, CancellationToken token);
}
