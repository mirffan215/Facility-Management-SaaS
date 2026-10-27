using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Fms.Spaces.Persistence;
using Fms.Spaces.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fms.Spaces;

public static class SpacesServiceCollectionExtensions
{
    public static IServiceCollection AddFmsSpaces(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<SpacesDbContext>((sp, options) => options
            .UseSqlServer(connectionString, SpacesDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.TryAddSingleton<IFileScanner, NoOpFileScanner>();
        services.AddScoped<SpaceService>();
        services.AddScoped<PlanService>();
        services.AddScoped<AllocationService>();
        services.AddHealthChecks().AddDbContextCheck<SpacesDbContext>("spaces-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, SpacesReportProvider>();
        return services;
    }
}
