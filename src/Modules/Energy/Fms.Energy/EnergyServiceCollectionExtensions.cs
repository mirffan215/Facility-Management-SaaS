using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Energy.Persistence;
using Fms.Energy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Energy;

public static class EnergyServiceCollectionExtensions
{
    public static IServiceCollection AddFmsEnergy(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<EnergyDbContext>((sp, options) => options
            .UseSqlServer(connectionString, EnergyDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<MeterService>();
        services.AddScoped<ReadingService>();
        services.AddScoped<ConsumptionService>();
        services.AddScoped<TariffService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<AlarmService>();
        services.AddScoped<AlarmEvaluator>();
        services.AddSingleton<EnergyMonitor>();
        services.AddHealthChecks().AddDbContextCheck<EnergyDbContext>("energy-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(EnergyDatasets.All(sp.GetRequiredService<EnergyDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, EnergyReportProvider>();
        return services;
    }
}
