using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fms.Vendors.Persistence;

/// <summary>Used by `dotnet ef` only. The connection string is a placeholder; nothing connects at design time.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VendorsDbContext>
{
    public VendorsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VendorsDbContext>()
                .UseSqlServer("Server=.;Database=Fms;Trusted_Connection=True;TrustServerCertificate=True", VendorsDbContextOptions.Sql).Options,
            new TenantContext());
}

public static class VendorsDbContextOptions
{
    /// <summary>Own migrations history table, in the module's schema, so module migrations are independent.</summary>
    public static void Sql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o) =>
        o.MigrationsHistoryTable("__EFMigrationsHistory", VendorsDbContext.Schema).EnableRetryOnFailure();
}
