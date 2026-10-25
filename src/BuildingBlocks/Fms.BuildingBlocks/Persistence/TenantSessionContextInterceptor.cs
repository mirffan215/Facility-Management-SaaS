using System.Data.Common;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fms.BuildingBlocks.Persistence;

/// <summary>
/// Copies the current tenant into SQL Server's SESSION_CONTEXT every time a connection is opened (pooled connections included),
/// which the row-level-security policy reads. This is the database-side backstop to the EF query filter: a query that forgets
/// the filter, or raw SQL, still only sees the current tenant's rows. No tenant means no rows.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = "EXEC sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 0; " +
                               "EXEC sp_set_session_context @key = N'IsPlatform', @value = @isPlatform, @read_only = 0;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Build(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new SqlParameter("@tenantId", (object?)tenant.TenantId ?? DBNull.Value));
        command.Parameters.Add(new SqlParameter("@isPlatform", tenant.IsPlatform));
        return command;
    }
}
