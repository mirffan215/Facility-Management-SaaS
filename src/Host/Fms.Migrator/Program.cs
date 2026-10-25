using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
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
await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(migratorConnection).Options, tenantContext);

Console.WriteLine("Applying migrations...");
await db.Database.MigrateAsync();

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

var appLogin = config["FMS_APP_LOGIN"];
var appPassword = config["FMS_APP_LOGIN_PASSWORD"];
if (!string.IsNullOrEmpty(appLogin) && !string.IsNullOrEmpty(appPassword))
{
    await AppLogin.EnsureAsync(migratorConnection, appLogin, appPassword);
    Console.WriteLine($"Application login '{appLogin}' ready.");
}

Console.WriteLine("Done.");

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
        var sql = $"""
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
