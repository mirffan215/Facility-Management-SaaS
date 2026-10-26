using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Fms.Vendors.Persistence;
using Fms.Vendors.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fms.Vendors;

public static class VendorsServiceCollectionExtensions
{
    public static IServiceCollection AddFmsVendors(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<VendorsDbContext>((sp, options) => options
            .UseSqlServer(connectionString, VendorsDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.TryAddSingleton<IFileScanner, NoOpFileScanner>();
        services.AddScoped<VendorService>();
        services.AddScoped<ContractService>();
        services.AddScoped<ContractDocumentService>();
        services.AddScoped<IVendorLookup, VendorLookup>();
        services.AddScoped<ContractAlerts>();
        services.AddSingleton<ContractMonitor>();
        services.AddScoped<IOutboxHandler, ContractAlertHandler>();
        services.AddHealthChecks().AddDbContextCheck<VendorsDbContext>("vendors-db", tags: ["ready"]);
        return services;
    }
}
