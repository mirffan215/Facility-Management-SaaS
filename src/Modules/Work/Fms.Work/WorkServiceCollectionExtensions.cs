using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Persistence;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fms.Work;

public static class WorkServiceCollectionExtensions
{
    public static IServiceCollection AddFmsWork(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<WorkDbContext>((sp, options) => options
            .UseSqlServer(connectionString, WorkDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.TryAddSingleton<Fms.BuildingBlocks.Storage.IFileScanner, Fms.BuildingBlocks.Storage.NoOpFileScanner>();
        services.AddOptions<WorkOptions>().BindConfiguration(WorkOptions.SectionName);
        services.AddScoped<WorkEvidenceService>();
        services.AddScoped<WorkFilesService>();
        services.AddScoped<ChecklistTemplateService>();
        services.AddScoped<OfflineJobsService>();
        services.AddScoped<Fms.BuildingBlocks.Identity.IWorkOrderLookup, WorkOrderLookup>();
        services.AddScoped<PmEngine>();
        services.AddScoped<PmService>();
        services.AddSingleton<PmGenerator>();
        services.AddScoped<SlaClock>();
        services.AddScoped<Fms.BuildingBlocks.Messaging.IOutboxHandler, WorkOrderEventHandler>();
        services.AddScoped<Fms.BuildingBlocks.Messaging.IOutboxHandler, SlaAlertHandler>();
        services.AddSingleton<SlaMonitor>();
        services.AddScoped<WorkOrderService>();
        services.AddScoped<PortalWorkService>();
        services.AddScoped<WorkSetupService>();
        services.AddScoped<SlaSetupService>();
        services.AddHealthChecks().AddDbContextCheck<WorkDbContext>("work-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(WorkDatasets.All(sp.GetRequiredService<Persistence.WorkDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, WorkReportProvider>();
        return services;
    }
}
