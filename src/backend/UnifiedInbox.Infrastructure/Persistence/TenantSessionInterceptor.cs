using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using UnifiedInbox.Application;

namespace UnifiedInbox.Infrastructure.Persistence;

public sealed class TenantSessionInterceptor(ICurrentTenant current) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.current_tenant', @tenant, false)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "tenant"; parameter.Value = current.TenantId?.ToString() ?? ""; command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
