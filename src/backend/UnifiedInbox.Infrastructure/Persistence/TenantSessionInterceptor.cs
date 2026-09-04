namespace UnifiedInbox.Infrastructure.Persistence;

// Tenant context is transaction-local and is established only by TenantExecutionScope.
// This retained type prevents older registrations from silently restoring connection-wide state.
public sealed class TenantSessionInterceptor;
