using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Inventory.Persistence;
using Fms.Inventory.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Inventory;

public static class InventoryServiceCollectionExtensions
{
    public static IServiceCollection AddFmsInventory(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<InventoryDbContext>((sp, options) => options
            .UseSqlServer(connectionString, InventoryDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<StockLedger>();
        services.AddScoped<CatalogService>();
        services.AddScoped<StockService>();
        services.AddScoped<WorkPartsService>();
        services.AddScoped<IWorkPartsLookup>(sp => sp.GetRequiredService<WorkPartsService>());
        services.AddScoped<IPmPartsReserver>(sp => sp.GetRequiredService<WorkPartsService>());
        services.AddScoped<StockTakeService>();
        services.AddScoped<IOutboxHandler, WorkOrderEndedHandler>();
        services.AddScoped<IOutboxHandler, LowStockHandler>();
        services.AddHealthChecks().AddDbContextCheck<InventoryDbContext>("inventory-db", tags: ["ready"]);
        return services;
    }
}
