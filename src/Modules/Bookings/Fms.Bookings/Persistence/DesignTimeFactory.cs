using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fms.Bookings.Persistence;

/// <summary>Used by `dotnet ef` only. The connection string is a placeholder; nothing connects at design time.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BookingsDbContext>
{
    public BookingsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookingsDbContext>()
                .UseSqlServer("Server=.;Database=Fms;Trusted_Connection=True;TrustServerCertificate=True", BookingsDbContextOptions.Sql).Options,
            new TenantContext());
}

public static class BookingsDbContextOptions
{
    /// <summary>Own migrations history table, in the module's schema, so module migrations are independent.</summary>
    public static void Sql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o) =>
        o.MigrationsHistoryTable("__EFMigrationsHistory", BookingsDbContext.Schema).EnableRetryOnFailure();
}
