using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Reporting.Persistence;
using Fms.Reporting.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Reporting;

public static class ReportingServiceCollectionExtensions
{
    public static IServiceCollection AddFmsReporting(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<ReportingDbContext>((sp, options) => options
            .UseSqlServer(connectionString, ReportingDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));
        services.AddScoped<ReportService>();
        services.AddScoped<FeedService>();
        services.AddHealthChecks().AddDbContextCheck<ReportingDbContext>("reporting-db", tags: ["ready"]);
        return services;
    }
}
