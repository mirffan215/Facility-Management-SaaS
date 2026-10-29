using System.Diagnostics;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

/// <summary>Runs the real migrator process against a clean database, twice, as the compose/Helm job would.</summary>
[Collection(SqlCollection.Name)]
public sealed class MigratorTests(SqlServerFixture sql)
{
    private (int ExitCode, string Output) RunMigrator(string database, Guid tenant, string admin, string extraArgs = "", IDictionary<string, string>? env = null)
    {
        // Run it from its own build output, where its full dependency set lives (the project reference builds it first).
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fms.sln"))) root = root.Parent;
        var dll = Path.Combine(root!.FullName, "src", "Host", "Fms.Migrator", "bin", baseDir.Parent!.Name, baseDir.Name, "Fms.Migrator.dll");
        var builder = new SqlConnectionStringBuilder(sql.OwnerConnectionString) { InitialCatalog = database };
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\" {extraArgs}") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["ConnectionStrings__Migrator"] = builder.ConnectionString;
        info.Environment["Deployment__Mode"] = "OnPrem";
        info.Environment["Deployment__SingleTenantId"] = tenant.ToString();
        info.Environment["Deployment__LocalStoragePath"] = "/tmp";
        info.Environment["FMS_INITIAL_ADMIN"] = admin;
        info.Environment["FMS_APP_LOGIN"] = "fms_mig_app";
        info.Environment["FMS_APP_LOGIN_PASSWORD"] = "Mig#Passw0rd!2026";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) info.Environment[k] = v;

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
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'bookings' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'visitors'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'visitors' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'reporting'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'reporting' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'compliance'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'compliance' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'energy'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'energy' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'projects'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'projects' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'leases'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'leases' AND t.name <> N'__EFMigrationsHistory'),
                   (SELECT COUNT(DISTINCT p.target_object_id) FROM sys.security_predicates p JOIN sys.objects o ON o.object_id = p.target_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'ai'),
                   (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'ai' AND t.name <> N'__EFMigrationsHistory')
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(7, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(reader.GetInt32(5), reader.GetInt32(4)); // every facilities table is under row-level security
        Assert.Equal(reader.GetInt32(7), reader.GetInt32(6)); // every work table is under row-level security too
        Assert.Equal(23, reader.GetInt32(7)); // work orders, history, comments, categories, settings, SLA (5), checklists (3), time, costs, voids, signatures, attachments
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
        Assert.Equal(reader.GetInt32(21), reader.GetInt32(20)); // and every visitors table
        Assert.Equal(3, reader.GetInt32(21)); // visits, watchlist, settings
        Assert.Equal(reader.GetInt32(23), reader.GetInt32(22)); // and every reporting table
        Assert.Equal(1, reader.GetInt32(23)); // feed tokens
        Assert.Equal(reader.GetInt32(25), reader.GetInt32(24)); // and every compliance table
        Assert.Equal(6, reader.GetInt32(25)); // obligations, completions, documents, permit types, permits, permit events
        Assert.Equal(reader.GetInt32(27), reader.GetInt32(26)); // and every energy table
        Assert.Equal(7, reader.GetInt32(27));
        Assert.Equal(reader.GetInt32(29), reader.GetInt32(28)); // and every projects table
        Assert.Equal(6, reader.GetInt32(29)); // projects, phases, tasks, milestones, budget lines, links
        Assert.Equal(reader.GetInt32(31), reader.GetInt32(30)); // and every leases table
        Assert.Equal(2, reader.GetInt32(31)); // leases, documents
        Assert.Equal(reader.GetInt32(33), reader.GetInt32(32)); // and every ai table
        Assert.Equal(3, reader.GetInt32(33)); // settings, usage, feedback
        Assert.Equal(8, reader.GetInt32(5)); // Locations, CustomFieldDefinitions, AssetClasses, Assets, AssetDocuments, Meters, MeterReadings, MeterReadingVoids
    }

    [Fact]
    public async Task Check_mode_reports_what_would_run_changes_nothing_and_reports_nothing_once_up_to_date()
    {
        var database = "FmsChk" + Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.NewGuid();

        var before = RunMigrator(database, tenant, "corp:alice", "--check");
        Assert.True(before.ExitCode == 0, before.Output);
        Assert.Matches(@"Migrations pending: [1-9]\d*", before.Output);
        Assert.Contains("Work: ", before.Output);

        // Nothing was created by looking.
        await using (var master = new SqlConnection(sql.OwnerConnectionString))
        {
            await master.OpenAsync();
            await using var exists = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n", master);
            exists.Parameters.AddWithValue("@n", database);
            Assert.Equal(0, (int)(await exists.ExecuteScalarAsync())!);
        }

        Assert.True(RunMigrator(database, tenant, "corp:alice").ExitCode == 0);
        var after = RunMigrator(database, tenant, "corp:alice", "--check");
        Assert.True(after.ExitCode == 0, after.Output);
        Assert.Contains("Migrations pending: 0", after.Output);
    }

    [Fact]
    public async Task A_database_from_a_newer_release_is_refused_so_an_old_version_cannot_damage_it()
    {
        var database = "FmsNew" + Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.NewGuid();
        Assert.True(RunMigrator(database, tenant, "corp:alice").ExitCode == 0);

        await using (var conn = new SqlConnection(new SqlConnectionStringBuilder(sql.OwnerConnectionString) { InitialCatalog = database }.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("INSERT INTO work.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (N'20990101000000_FromTheFuture', N'99.0.0')", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var check = RunMigrator(database, tenant, "corp:alice", "--check");
        Assert.Equal(4, check.ExitCode);
        Assert.Contains("NEWER than this version", check.Output);
        Assert.Contains("20990101000000_FromTheFuture", check.Output);

        var run = RunMigrator(database, tenant, "corp:alice");
        Assert.Equal(4, run.ExitCode);
        Assert.Contains("Refusing to run", run.Output);
    }

    [Fact]
    public async Task Encrypt_existing_protects_details_stored_readable_before_a_key_was_set_once_and_changes_nothing_else()
    {
        var database = "FmsEnc" + Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.NewGuid();
        Assert.True(RunMigrator(database, tenant, "corp:alice").ExitCode == 0);

        var connection = new SqlConnectionStringBuilder(sql.OwnerConnectionString) { InitialCatalog = database }.ConnectionString;
        var context = new TenantContext();
        context.SetTenant(tenant);
        var id = Guid.NewGuid();
        await using (var db = new VisitorsDbContext(new DbContextOptionsBuilder<VisitorsDbContext>().UseSqlServer(connection, VisitorsDbContextOptions.Sql).AddInterceptors(new TenantSessionContextInterceptor(context)).Options, context))
        {
            db.Visits.Add(new Visit
            {
                Id = id, TenantId = tenant, HostId = Guid.NewGuid(), HostName = "Host", SiteId = Guid.NewGuid(), SiteName = "HQ", VisitorName = "Layla", VisitorEmail = "layla@acme.test", VisitorPhone = "0501234567",
                ExpectedAt = DateTimeOffset.UtcNow.AddHours(1), PassHash = new string('a', 64), CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var vendorId = Guid.NewGuid();
        await using (var vdb = new VendorsDbContext(new DbContextOptionsBuilder<VendorsDbContext>().UseSqlServer(connection, VendorsDbContextOptions.Sql).AddInterceptors(new TenantSessionContextInterceptor(context)).Options, context))
        {
            vdb.Vendors.Add(new Vendor { Id = vendorId, TenantId = tenant, Code = "V1", Name = "CoolAir", Email = "ops@coolair.test", Phone = "0111234567", CreatedAt = DateTimeOffset.UtcNow });
            vdb.Contacts.Add(new VendorContact { TenantId = tenant, VendorId = vendorId, Name = "Omar", Email = "omar@coolair.test", Phone = "0507654321", IsPrimary = true });
            await vdb.SaveChangesAsync();
        }

        async Task<string[]> StoredVendor()
        {
            await using var conn = new SqlConnection(connection);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("EXEC sp_set_session_context N'IsPlatform', 1; SELECT v.Email, v.Phone, c.Email, c.Phone FROM vendors.Vendors v JOIN vendors.Contacts c ON c.VendorId = v.Id WHERE v.Id = @id", conn);
            cmd.Parameters.AddWithValue("@id", vendorId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return Enumerable.Range(0, 4).Select(reader.GetString).ToArray();
        }

        Assert.Equal(["ops@coolair.test", "0111234567", "omar@coolair.test", "0507654321"], await StoredVendor());

        async Task<(string? Email, string? Phone)> Stored()
        {
            await using var conn = new SqlConnection(connection);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("EXEC sp_set_session_context N'IsPlatform', 1; SELECT VisitorEmail, VisitorPhone FROM visitors.Visits WHERE Id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1));
        }

        Assert.Equal(("layla@acme.test", "0501234567"), await Stored());

        var withoutKey = RunMigrator(database, tenant, "corp:alice", "--encrypt-existing");
        Assert.Equal(5, withoutKey.ExitCode);
        Assert.Equal(("layla@acme.test", "0501234567"), await Stored());
        Assert.Equal("ops@coolair.test", (await StoredVendor())[0]);

        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var env = new Dictionary<string, string> { ["Encryption__Keys__k1"] = key, ["Encryption__CurrentKeyId"] = "k1" };
        var first = RunMigrator(database, tenant, "corp:alice", "--encrypt-existing", env);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains("1 visit(s), 1 vendor(s) and 1 vendor contact(s)", first.Output);
        var (email, phone) = await Stored();
        Assert.StartsWith("enc1:k1:", email);
        Assert.StartsWith("enc1:k1:", phone);
        var vendorValues = await StoredVendor();
        Assert.All(vendorValues, v => Assert.StartsWith("enc1:k1:", v));

        var second = RunMigrator(database, tenant, "corp:alice", "--encrypt-existing", env);
        Assert.Contains("0 visit(s), 0 vendor(s) and 0 vendor contact(s)", second.Output); // already protected with the current key: nothing to do
        Assert.Equal((email, phone), await Stored());
        Assert.Equal(vendorValues, await StoredVendor());

        // After a rotation to a new key the same command re-protects everything with it.
        var rotated = new Dictionary<string, string> { ["Encryption__Keys__k1"] = key, ["Encryption__Keys__k2"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), ["Encryption__CurrentKeyId"] = "k2" };
        var third = RunMigrator(database, tenant, "corp:alice", "--encrypt-existing", rotated);
        Assert.Contains("1 visit(s), 1 vendor(s) and 1 vendor contact(s)", third.Output);
        Assert.All(await StoredVendor(), v => Assert.StartsWith("enc1:k2:", v));
    }
}
