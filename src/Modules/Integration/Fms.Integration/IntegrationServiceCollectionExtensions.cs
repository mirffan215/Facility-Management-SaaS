using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Integration.Persistence;
using Fms.Integration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Integration;

public static class IntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddFmsIntegration(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<IntegrationDbContext>((sp, options) => options
            .UseSqlServer(connectionString, IntegrationDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddOptions<ErpOptions>().BindConfiguration(ErpOptions.SectionName);
        services.AddSingleton<FakeErpAdapter>();
        services.AddSingleton<IErpAdapter>(sp => sp.GetRequiredService<FakeErpAdapter>());
        services.AddScoped<ErpSettingsService>();
        services.AddScoped<ErpAdminService>();
        services.AddSingleton<ErpSyncRunner>();
        services.AddScoped<IOutboxHandler, ErpExportHandler>();
        services.AddHealthChecks().AddDbContextCheck<IntegrationDbContext>("integration-db", tags: ["ready"]);
        return services;
    }
}
