using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Persistence;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Work;

public static class WorkServiceCollectionExtensions
{
    public static IServiceCollection AddFmsWork(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<WorkDbContext>((sp, options) => options
            .UseSqlServer(connectionString, WorkDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<WorkOrderService>();
        services.AddScoped<WorkSetupService>();
        services.AddHealthChecks().AddDbContextCheck<WorkDbContext>("work-db", tags: ["ready"]);
        return services;
    }
}
