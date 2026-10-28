using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Projects.Persistence;
using Fms.Projects.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Projects;

public static class ProjectsServiceCollectionExtensions
{
    public static IServiceCollection AddFmsProjects(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<ProjectsDbContext>((sp, options) => options
            .UseSqlServer(connectionString, ProjectsDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddScoped<ProjectService>();
        services.AddScoped<PlanService>();
        services.AddHealthChecks().AddDbContextCheck<ProjectsDbContext>("projects-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(ProjectsDatasets.All(sp.GetRequiredService<ProjectsDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, ProjectsReportProvider>();
        return services;
    }
}
