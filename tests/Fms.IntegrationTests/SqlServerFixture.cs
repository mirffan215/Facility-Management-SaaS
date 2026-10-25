using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Fms.Platform.Services;
using Fms.Platform.Security;
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
            await SystemRoleSync.EnsureAsync(owner, TenantA);
            await SystemRoleSync.EnsureAsync(owner, TenantB);
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

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlatformDbContext, PendingWrites> _pending = new();

    /// <summary>A context as the app would build it, with its own pending-writes buffer (see <see cref="PendingFor"/>).</summary>
    public PlatformDbContext App(Guid? tenantId, bool platform = false)
    {
        var pending = new PendingWrites();
        var db = Context(AppConnectionString, tenantId, platform, pending);
        _pending.Add(db, pending);
        return db;
    }

    public PendingWrites PendingFor(PlatformDbContext db) => _pending.TryGetValue(db, out var p) ? p : throw new InvalidOperationException("Context was not created by the fixture.");

    private static PlatformDbContext Context(string connectionString, Guid? tenantId, bool platform, PendingWrites? pending = null)
    {
        var tenant = new TenantContext();
        if (tenantId is { } id) tenant.SetTenant(id);
        if (platform) tenant.BeginPlatformScope();

        return new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenant))
                .Options,
            tenant, pending);
    }

    /// <summary>A fresh migrated database with both tenants, for tests that must not see other tests' rows (e.g. workers that claim any due message).</summary>
    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var database = "FmsIso" + Guid.NewGuid().ToString("N")[..10];
        var owner = new SqlConnectionStringBuilder(OwnerConnectionString) { InitialCatalog = database }.ConnectionString;

        await using (var owned = Context(owner, null, platform: true))
        {
            await owned.Database.MigrateAsync();
            owned.Tenants.Add(new Tenant { Id = TenantA, Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow });
            owned.Tenants.Add(new Tenant { Id = TenantB, Name = "Tenant B", CreatedAt = DateTimeOffset.UtcNow });
            await owned.SaveChangesAsync();
        }

        await using var conn = new SqlConnection(owner);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"CREATE USER [{AppLogin}] FOR LOGIN [{AppLogin}]; ALTER ROLE db_datareader ADD MEMBER [{AppLogin}]; ALTER ROLE db_datawriter ADD MEMBER [{AppLogin}];", conn);
        await cmd.ExecuteNonQueryAsync();

        return new SqlConnectionStringBuilder(owner) { UserID = AppLogin, Password = AppPassword }.ConnectionString;
    }

    public static User NewUser(string subject = "sub", string provider = "entra") =>
        new() { Provider = provider, Subject = subject, CreatedAt = DateTimeOffset.UtcNow, LastSignInAt = DateTimeOffset.UtcNow };
}
