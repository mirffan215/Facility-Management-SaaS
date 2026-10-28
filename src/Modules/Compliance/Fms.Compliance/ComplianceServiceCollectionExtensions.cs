using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Compliance.Persistence;
using Fms.Compliance.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Compliance;

public static class ComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddFmsCompliance(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<ComplianceDbContext>((sp, options) => options
            .UseSqlServer(connectionString, ComplianceDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<ObligationService>();
        services.AddScoped<CompletionDocumentService>();
        services.AddScoped<PermitTypeService>();
        services.AddScoped<PermitService>();
        services.AddScoped<ComplianceAlerts>();
        services.AddSingleton<ComplianceMonitor>();
        services.AddHealthChecks().AddDbContextCheck<ComplianceDbContext>("compliance-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(ComplianceDatasets.All(sp.GetRequiredService<ComplianceDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, ComplianceReportProvider>();
        return services;
    }
}
