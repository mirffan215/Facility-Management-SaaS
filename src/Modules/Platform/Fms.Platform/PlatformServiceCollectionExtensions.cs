using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Persistence;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fms.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform database and replaces the config-backed tenant directory with the database-backed one.
    /// The application connects as a least-privileged login; migrations run separately under the migrator.
    /// </summary>
    public static IServiceCollection AddFmsPlatform(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<PlatformDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.Replace(ServiceDescriptor.Scoped<ITenantDirectory, DbTenantDirectory>());
        services.AddScoped<IUserProvisioner, UserProvisioner>();
        services.AddHealthChecks().AddDbContextCheck<PlatformDbContext>("platform-db", tags: ["ready"]);
        return services;
    }
}
