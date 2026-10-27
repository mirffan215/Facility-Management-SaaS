using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Persistence;
using Fms.Inventory.Persistence;
using Fms.Integration.Persistence;
using Fms.Purchasing.Persistence;
using Fms.Bookings.Persistence;
using Fms.Reporting.Persistence;
using Fms.Visitors.Persistence;
using Fms.Spaces.Persistence;
using Fms.Vendors.Persistence;
using Fms.Work.Persistence;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Applies migrations under an owner login, seeds the on-prem tenant, and (optionally) provisions the least-privileged
// application login. Run as a one-shot container/job before the API starts. Idempotent.
var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();

var migratorConnection = config.GetConnectionString("Migrator")
    ?? throw new InvalidOperationException("ConnectionStrings:Migrator is required (an owner login).");

var tenantContext = new TenantContext();
using var platformScope = tenantContext.BeginPlatformScope();
await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(migratorConnection)
    .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);

// Every module keeps its own migration history. The platform goes first (the others extend its row-level-security policy).
var contexts = new List<(string Name, DbContext Context)> { ("Platform", db) };
await using var facilitiesDb = new FacilitiesDbContext(
    new DbContextOptionsBuilder<FacilitiesDbContext>().UseSqlServer(migratorConnection, FacilitiesDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Facilities", facilitiesDb));
await using var workDb = new WorkDbContext(
    new DbContextOptionsBuilder<WorkDbContext>().UseSqlServer(migratorConnection, WorkDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Work", workDb));
await using var inventoryDb = new InventoryDbContext(
    new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(migratorConnection, InventoryDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Inventory", inventoryDb));
await using var vendorsDb = new VendorsDbContext(
    new DbContextOptionsBuilder<VendorsDbContext>().UseSqlServer(migratorConnection, VendorsDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Vendors", vendorsDb));
await using var spacesDb = new SpacesDbContext(
    new DbContextOptionsBuilder<SpacesDbContext>().UseSqlServer(migratorConnection, SpacesDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Spaces", spacesDb));
await using var bookingsDb = new BookingsDbContext(
    new DbContextOptionsBuilder<BookingsDbContext>().UseSqlServer(migratorConnection, BookingsDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Bookings", bookingsDb));
await using var visitorsDb = new VisitorsDbContext(
    new DbContextOptionsBuilder<VisitorsDbContext>().UseSqlServer(migratorConnection, VisitorsDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Visitors", visitorsDb));
await using var reportingDb = new ReportingDbContext(
    new DbContextOptionsBuilder<ReportingDbContext>().UseSqlServer(migratorConnection, ReportingDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Reporting", reportingDb));
await using var purchasingDb = new PurchasingDbContext(
    new DbContextOptionsBuilder<PurchasingDbContext>().UseSqlServer(migratorConnection, PurchasingDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Purchasing", purchasingDb));
await using var integrationDb = new IntegrationDbContext(
    new DbContextOptionsBuilder<IntegrationDbContext>().UseSqlServer(migratorConnection, IntegrationDbContextOptions.Sql)
        .AddInterceptors(new TenantSessionContextInterceptor(tenantContext)).Options, tenantContext);
contexts.Add(("Integration", integrationDb));

// A database that has seen migrations this build does not know belongs to a newer release. Running an older release against it would
// corrupt data silently, so refuse (both for --check and for a real run). Restoring the pre-upgrade backup is the way back.
var newerThanThisBuild = new List<string>();
var pending = new List<string>();
foreach (var (name, context) in contexts)
{
    var known = context.Database.GetMigrations().ToHashSet();
    var applied = await DatabaseExists(context) ? (await context.Database.GetAppliedMigrationsAsync()).ToList() : [];
    newerThanThisBuild.AddRange(applied.Where(m => !known.Contains(m)).Select(m => $"{name}: {m}"));
    pending.AddRange(known.Except(applied).Select(m => $"{name}: {m}"));
}

if (config["check"] is not null || args.Contains("--check"))
{
    Console.WriteLine($"Migrations pending: {pending.Count}");
    foreach (var m in pending) Console.WriteLine($"  + {m}");
    if (newerThanThisBuild.Count > 0)
    {
        Console.Error.WriteLine("This database is NEWER than this version (it has migrations this version does not know):");
        foreach (var m in newerThanThisBuild) Console.Error.WriteLine($"  ! {m}");
        return 4;
    }

    return 0;
}

if (newerThanThisBuild.Count > 0)
{
    Console.Error.WriteLine("Refusing to run: the database is newer than this version. Restore the pre-upgrade backup or install the newer version.");
    foreach (var m in newerThanThisBuild) Console.Error.WriteLine($"  ! {m}");
    return 4;
}

Console.WriteLine("Applying migrations...");
foreach (var (name, context) in contexts)
{
    await context.Database.MigrateAsync();
}

static async Task<bool> DatabaseExists(DbContext context) => await context.Database.CanConnectAsync();


var deployment = config.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>() ?? new DeploymentOptions();
if (deployment.Mode == DeploymentMode.OnPrem)
{
    var id = deployment.SingleTenantId ?? throw new InvalidOperationException("Deployment:SingleTenantId is required for on-prem.");
    if (!await db.Tenants.AnyAsync(t => t.Id == id))
    {
        db.Tenants.Add(new Tenant { Id = id, Name = config["Deployment:TenantName"] ?? "Default", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine($"Seeded on-prem tenant {id}.");
    }
}

// Keep every tenant's system roles current and, when asked, make the first administrator. A new tenant has nobody who can
// grant roles, so this is the only way in; later admins are made through the API.
foreach (var tenantId in await db.Tenants.Select(t => t.Id).ToListAsync())
{
    await SystemRoleSync.EnsureAsync(db, tenantId);
}

if (config["FMS_INITIAL_ADMIN"] is { Length: > 0 } initialAdmin)
{
    var parts = initialAdmin.Split(':', 2);
    var tenantId = deployment.Mode == DeploymentMode.OnPrem
        ? deployment.SingleTenantId!.Value
        : Guid.Parse(config["FMS_INITIAL_ADMIN_TENANT"] ?? throw new InvalidOperationException("FMS_INITIAL_ADMIN_TENANT is required in SaaS mode."));
    if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException("FMS_INITIAL_ADMIN must be '<provider>:<subject>'.");
    }

    await InitialAdmin.EnsureAsync(db, tenantId, parts[0], parts[1]);
    Console.WriteLine($"Administrator '{initialAdmin}' ready in tenant {tenantId}.");
}

var appLogin = config["FMS_APP_LOGIN"];
var appPassword = config["FMS_APP_LOGIN_PASSWORD"];
if (!string.IsNullOrEmpty(appLogin) && !string.IsNullOrEmpty(appPassword))
{
    await AppLogin.EnsureAsync(migratorConnection, appLogin, appPassword);
    Console.WriteLine($"Application login '{appLogin}' ready.");
}

Console.WriteLine("Done.");
return 0;

internal static class AppLogin
{
    /// <summary>Creates a login/user that can read and write data but cannot change schema, policies or triggers.</summary>
    public static async Task EnsureAsync(string ownerConnection, string login, string password)
    {
        if (login.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException("Login may contain letters, digits and underscore only.");
        }

        await using var conn = new SqlConnection(ownerConnection);
        await conn.OpenAsync();

        // Passwords cannot be parameterised in CREATE LOGIN; quote defensively.
        var quoted = password.Replace("'", "''", StringComparison.Ordinal);

        // Azure SQL Database has no server-level logins in a user database: the application gets a contained database user instead.
        await using var edition = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", conn);
        var azureSqlDatabase = (int)(await edition.ExecuteScalarAsync())! == 5;
        var sql = azureSqlDatabase
            ? $"""
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{login}') CREATE USER [{login}] WITH PASSWORD = N'{quoted}';
                ELSE ALTER USER [{login}] WITH PASSWORD = N'{quoted}';
                ALTER ROLE db_datareader ADD MEMBER [{login}];
                ALTER ROLE db_datawriter ADD MEMBER [{login}];
                """
            : $"""
                IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') CREATE LOGIN [{login}] WITH PASSWORD = N'{quoted}', CHECK_POLICY = OFF;
                ELSE ALTER LOGIN [{login}] WITH PASSWORD = N'{quoted}';
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{login}') CREATE USER [{login}] FOR LOGIN [{login}];
                ALTER ROLE db_datareader ADD MEMBER [{login}];
                ALTER ROLE db_datawriter ADD MEMBER [{login}];
                """;
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
