using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Leases.Persistence;
using Fms.Leases.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Leases;

public static class LeasesServiceCollectionExtensions
{
    public static IServiceCollection AddFmsLeases(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<LeasesDbContext>((sp, options) => options
            .UseSqlServer(connectionString, LeasesDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<LeaseService>();
        services.AddScoped<LeaseAlerts>();
        services.AddScoped<LeaseDocumentService>();
        services.AddSingleton<LeaseMonitor>();
        services.AddHealthChecks().AddDbContextCheck<LeasesDbContext>("leases-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(LeasesDatasets.All(sp.GetRequiredService<LeasesDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, LeasesReportProvider>();
        return services;
    }
}
