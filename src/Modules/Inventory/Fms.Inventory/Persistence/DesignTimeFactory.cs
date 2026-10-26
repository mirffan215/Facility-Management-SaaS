using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fms.Inventory.Persistence;

/// <summary>Used by `dotnet ef` only. The connection string is a placeholder; nothing connects at design time.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseSqlServer("Server=.;Database=Fms;Trusted_Connection=True;TrustServerCertificate=True", InventoryDbContextOptions.Sql).Options,
            new TenantContext());
}

public static class InventoryDbContextOptions
{
    /// <summary>Own migrations history table, in the module's schema, so module migrations are independent.</summary>
    public static void Sql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o) =>
        o.MigrationsHistoryTable("__EFMigrationsHistory", InventoryDbContext.Schema).EnableRetryOnFailure();
}
