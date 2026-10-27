using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

/// <summary>Runs the real migrator process against a clean database, twice, as the compose/Helm job would.</summary>
[Collection(SqlCollection.Name)]
public sealed class MigratorTests(SqlServerFixture sql)
{
    private (int ExitCode, string Output) RunMigrator(string database, Guid tenant, string admin)
    {
        // Run it from its own build output, where its full dependency set lives (the project reference builds it first).
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fms.sln"))) root = root.Parent;
        var dll = Path.Combine(root!.FullName, "src", "Host", "Fms.Migrator", "bin", baseDir.Parent!.Name, baseDir.Name, "Fms.Migrator.dll");
        var builder = new SqlConnectionStringBuilder(sql.OwnerConnectionString) { InitialCatalog = database };
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["ConnectionStrings__Migrator"] = builder.ConnectionString;
        info.Environment["Deployment__Mode"] = "OnPrem";
        info.Environment["Deployment__SingleTenantId"] = tenant.ToString();
        info.Environment["Deployment__LocalStoragePath"] = "/tmp";
        info.Environment["FMS_INITIAL_ADMIN"] = admin;
        info.Environment["FMS_APP_LOGIN"] = "fms_mig_app";
        info.Environment["FMS_APP_LOGIN_PASSWORD"] = "Mig#Passw0rd!2026";

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task Migrator_sets_up_a_clean_database_and_can_be_run_again()
    {
        var database = "FmsMig" + Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.NewGuid();

        var first = RunMigrator(database, tenant, "corp:alice");
        Assert.True(first.ExitCode == 0, first.Output);
        var second = RunMigrator(database, tenant, "corp:alice");
        Assert.True(second.ExitCode == 0, second.Output);

        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(sql.OwnerConnectionString) { InitialCatalog = database }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            EXEC sp_set_session_context N'IsPlatform', 1;
            SELECT (SELECT COUNT(*) FROM Tenants), (SELECT COUNT(*) FROM Roles), (SELECT COUNT(*) FROM UserRoles), (SELECT COUNT(*) FROM Users),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'facilities'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'facilities' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'work'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'work' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'inventory'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'inventory' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'vendors'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'vendors' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'purchasing'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'purchasing' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'integration'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'integration' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'spaces'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'spaces' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'bookings'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'bookings' AND t.name <> N'__EFMigrationsHistory')
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(6, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(reader.GetInt32(5), reader.GetInt32(4)); // every facilities table is under row-level security
        Assert.Equal(reader.GetInt32(7), reader.GetInt32(6)); // every work table is under row-level security too
        Assert.Equal(22, reader.GetInt32(7)); // work orders, history, comments, categories, settings, SLA (5), checklists (3), time, costs, voids, signatures, attachments
        Assert.Equal(reader.GetInt32(9), reader.GetInt32(8)); // and every inventory table
        Assert.Equal(8, reader.GetInt32(9)); // stores, items, levels, movements, reservations, template parts, stock takes, stock take lines
        Assert.Equal(reader.GetInt32(11), reader.GetInt32(10)); // and every vendors table
        Assert.Equal(7, reader.GetInt32(11)); // vendors, contacts, contracts, coverage, service targets, contract documents, portal users
        Assert.Equal(reader.GetInt32(13), reader.GetInt32(12)); // and every purchasing table
        Assert.Equal(6, reader.GetInt32(13)); // settings, requests, request lines, orders, order lines, goods receipts
        Assert.Equal(reader.GetInt32(15), reader.GetInt32(14)); // and every integration table
        Assert.Equal(2, reader.GetInt32(15)); // settings, export items
        Assert.Equal(reader.GetInt32(17), reader.GetInt32(16)); // and every spaces table
        Assert.Equal(4, reader.GetInt32(17)); // profiles, plans, pins, allocations
        Assert.Equal(reader.GetInt32(19), reader.GetInt32(18)); // and every bookings table
        Assert.Equal(5, reader.GetInt32(19)); // resources, bookings, calendar links, feed tokens, calendar settings
        Assert.Equal(8, reader.GetInt32(5)); // Locations, CustomFieldDefinitions, AssetClasses, Assets, AssetDocuments, Meters, MeterReadings, MeterReadingVoids
    }
}
