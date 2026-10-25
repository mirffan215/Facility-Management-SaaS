using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Persistence;
using Fms.Facilities.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Facilities;

public static class FacilitiesServiceCollectionExtensions
{
    public static IServiceCollection AddFmsFacilities(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<FacilitiesDbContext>((sp, options) => options
            .UseSqlServer(connectionString, FacilitiesDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<LocationService>();
        services.AddScoped<CustomFieldService>();
        services.AddHealthChecks().AddDbContextCheck<FacilitiesDbContext>("facilities-db", tags: ["ready"]);
        return services;
    }
}
