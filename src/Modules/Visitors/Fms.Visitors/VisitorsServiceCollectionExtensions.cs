using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Visitors.Persistence;
using Fms.Visitors.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Visitors;

public static class VisitorsServiceCollectionExtensions
{
    public static IServiceCollection AddFmsVisitors(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<VisitorsDbContext>((sp, options) => options
            .UseSqlServer(connectionString, VisitorsDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<PassService>();
        services.AddScoped<VisitService>();
        services.AddScoped<WatchlistService>();
        services.AddScoped<VisitorSettingsService>();
        services.AddScoped<IOutboxHandler, VisitorArrivedHandler>();
        services.AddScoped<VisitHousekeeping>();
        services.AddSingleton<VisitMonitor>();
        services.AddHealthChecks().AddDbContextCheck<VisitorsDbContext>("visitors-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(VisitorsDatasets.All(sp.GetRequiredService<Persistence.VisitorsDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, VisitorsReportProvider>();
        return services;
    }
}
