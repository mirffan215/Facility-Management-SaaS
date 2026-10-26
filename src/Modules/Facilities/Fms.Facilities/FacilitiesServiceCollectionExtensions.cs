using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Persistence;
using Fms.Facilities.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddScoped<AssetClassService>();
        services.AddScoped<AssetService>();
        services.AddScoped<MeterService>();
        services.AddScoped<Fms.BuildingBlocks.Identity.IFacilityLookup, FacilityLookup>();
        services.AddScoped<DocumentService>();
        services.AddScoped<ImportExportService>();
        services.TryAddSingleton<IFileScanner, NoOpFileScanner>();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<FacilitiesOptions>, FacilitiesOptionsValidator>();
        services.AddOptions<FacilitiesOptions>().BindConfiguration(FacilitiesOptions.SectionName).ValidateOnStart();
        services.AddSingleton<AssetLabelService>();
        services.AddHealthChecks().AddDbContextCheck<FacilitiesDbContext>("facilities-db", tags: ["ready"]);
        return services;
    }
}
