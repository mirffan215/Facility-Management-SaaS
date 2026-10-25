using Microsoft.EntityFrameworkCore.Migrations;

namespace Fms.BuildingBlocks.Persistence;

/// <summary>
/// Row-level security for tenant-owned tables. Call from a migration for every table that has a TenantId column. Filter and
/// block predicates make SQL Server itself refuse to read, insert, update or delete rows of another tenant, whatever the
/// application code does. The policy reads SESSION_CONTEXT, which <see cref="TenantSessionContextInterceptor"/> sets.
/// Platform scope (migrations, schedulers) sees all rows.
/// </summary>
public static class TenantSecurityMigrationExtensions
{
    private const string Policy = "security.TenantIsolation";

    public static MigrationBuilder ProtectTenantTable(this MigrationBuilder migrationBuilder, string table)
    {
        ValidateName(table);

        migrationBuilder.Sql("IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');");
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'security.TenantPredicate') IS NULL
            EXEC(N'CREATE FUNCTION security.TenantPredicate(@TenantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS Allowed
                WHERE @TenantId = TRY_CAST(SESSION_CONTEXT(N''TenantId'') AS uniqueidentifier)
                   OR TRY_CAST(SESSION_CONTEXT(N''IsPlatform'') AS bit) = 1');
            """);

        var predicates = $"""
            ADD FILTER PREDICATE security.TenantPredicate(TenantId) ON dbo.[{table}],
            ADD BLOCK PREDICATE security.TenantPredicate(TenantId) ON dbo.[{table}] AFTER INSERT,
            ADD BLOCK PREDICATE security.TenantPredicate(TenantId) ON dbo.[{table}] AFTER UPDATE,
            ADD BLOCK PREDICATE security.TenantPredicate(TenantId) ON dbo.[{table}] BEFORE UPDATE,
            ADD BLOCK PREDICATE security.TenantPredicate(TenantId) ON dbo.[{table}] BEFORE DELETE
            """;

        migrationBuilder.Sql($"""
            IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantIsolation')
                EXEC(N'CREATE SECURITY POLICY {Policy} {predicates.Replace("'", "''")} WITH (STATE = ON)');
            ELSE
                EXEC(N'ALTER SECURITY POLICY {Policy} {predicates.Replace("'", "''")}');
            """);

        return migrationBuilder;
    }

    public static MigrationBuilder UnprotectTenantTable(this MigrationBuilder migrationBuilder, string table)
    {
        ValidateName(table);
        migrationBuilder.Sql($"""
            IF EXISTS (SELECT 1 FROM sys.security_predicates WHERE object_id = OBJECT_ID(N'dbo.{table}'))
            ALTER SECURITY POLICY {Policy}
                DROP FILTER PREDICATE ON dbo.[{table}],
                DROP BLOCK PREDICATE ON dbo.[{table}] AFTER INSERT,
                DROP BLOCK PREDICATE ON dbo.[{table}] AFTER UPDATE,
                DROP BLOCK PREDICATE ON dbo.[{table}] BEFORE UPDATE,
                DROP BLOCK PREDICATE ON dbo.[{table}] BEFORE DELETE;
            """);
        return migrationBuilder;
    }

    private static void ValidateName(string table)
    {
        if (string.IsNullOrWhiteSpace(table) || !table.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("Table name must be letters, digits or underscore.", nameof(table));
        }
    }
}
