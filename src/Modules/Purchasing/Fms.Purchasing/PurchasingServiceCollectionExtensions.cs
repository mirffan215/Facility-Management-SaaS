using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Purchasing.Persistence;
using Fms.Purchasing.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Purchasing;

public static class PurchasingServiceCollectionExtensions
{
    public static IServiceCollection AddFmsPurchasing(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<PurchasingDbContext>((sp, options) => options
            .UseSqlServer(connectionString, PurchasingDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<Fms.BuildingBlocks.Identity.IPurchaseOrderLookup, PurchaseOrderLookup>();
        services.AddScoped<SettingsService>();
        services.AddScoped<RequestService>();
        services.AddScoped<OrderService>();
        services.AddScoped<PortalOrderService>();
        services.AddScoped<IOutboxHandler, PurchasingEventHandler>();
        services.AddScoped<IOutboxHandler, OrderSyncedHandler>();
        services.AddHealthChecks().AddDbContextCheck<PurchasingDbContext>("purchasing-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(PurchasingDatasets.All(sp.GetRequiredService<Persistence.PurchasingDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, PurchasingReportProvider>();
        return services;
    }
}
