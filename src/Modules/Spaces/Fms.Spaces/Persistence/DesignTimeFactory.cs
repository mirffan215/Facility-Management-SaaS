using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fms.Spaces.Persistence;

/// <summary>Used by `dotnet ef` only. The connection string is a placeholder; nothing connects at design time.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SpacesDbContext>
{
    public SpacesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SpacesDbContext>()
                .UseSqlServer("Server=.;Database=Fms;Trusted_Connection=True;TrustServerCertificate=True", SpacesDbContextOptions.Sql).Options,
            new TenantContext());
}

public static class SpacesDbContextOptions
{
    /// <summary>Own migrations history table, in the module's schema, so module migrations are independent.</summary>
    public static void Sql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o) =>
        o.MigrationsHistoryTable("__EFMigrationsHistory", SpacesDbContext.Schema).EnableRetryOnFailure();
}
