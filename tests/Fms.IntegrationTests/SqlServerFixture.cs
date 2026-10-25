using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace Fms.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

/// <summary>
/// One real SQL Server for the whole run. Migrations are applied as the owner (sa); every test then connects as a restricted
/// login with read/write only, like production, because owners can change security objects and would hide real problems.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string AppLogin = "fms_app";
    private const string AppPassword = "App#Passw0rd!2026";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public static readonly Guid TenantA = Guid.NewGuid();
    public static readonly Guid TenantB = Guid.NewGuid();

    public string OwnerConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        OwnerConnectionString = _container.GetConnectionString();

        await using (var owner = Context(OwnerConnectionString, null, platform: true))
        {
            await owner.Database.MigrateAsync();
            owner.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow });
            owner.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
            await owner.SaveChangesAsync();
        }

        await using (var conn = new SqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"""
                CREATE LOGIN [{AppLogin}] WITH PASSWORD = N'{AppPassword}', CHECK_POLICY = OFF;
                CREATE USER [{AppLogin}] FOR LOGIN [{AppLogin}];
                ALTER ROLE db_datareader ADD MEMBER [{AppLogin}];
                ALTER ROLE db_datawriter ADD MEMBER [{AppLogin}];
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        AppConnectionString = new SqlConnectionStringBuilder(OwnerConnectionString) { UserID = AppLogin, Password = AppPassword }.ConnectionString;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public PlatformDbContext App(Guid? tenantId, bool platform = false) => Context(AppConnectionString, tenantId, platform);

    private static PlatformDbContext Context(string connectionString, Guid? tenantId, bool platform)
    {
        var tenant = new TenantContext();
        if (tenantId is { } id) tenant.SetTenant(id);
        if (platform) tenant.BeginPlatformScope();

        return new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenant))
                .Options,
            tenant);
    }

    public static User NewUser(string subject = "sub", string provider = "entra") =>
        new() { Provider = provider, Subject = subject, CreatedAt = DateTimeOffset.UtcNow, LastSignInAt = DateTimeOffset.UtcNow };
}
