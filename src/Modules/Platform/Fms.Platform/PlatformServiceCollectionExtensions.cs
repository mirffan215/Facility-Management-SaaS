using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Outbox;
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
    /// <summary>Registers a message handler. Handlers must be idempotent: delivery is at least once.</summary>
    public static IServiceCollection AddOutboxHandler<THandler>(this IServiceCollection services) where THandler : class, IOutboxHandler =>
        services.AddScoped<IOutboxHandler, THandler>();

    public static IServiceCollection AddFmsPlatform(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<PlatformDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.Replace(ServiceDescriptor.Scoped<ITenantDirectory, DbTenantDirectory>());
        services.AddScoped<IUserProvisioner, UserProvisioner>();
        services.AddScoped<PermissionService>();
        services.AddScoped<IPermissionService>(sp => sp.GetRequiredService<PermissionService>());
        services.AddScoped<Fms.BuildingBlocks.Security.ICurrentPermissions>(sp => sp.GetRequiredService<PermissionService>());
        services.AddScoped<Fms.BuildingBlocks.Identity.IUserDirectory, UserDirectory>();
        services.AddFmsPendingWrites();
        services.AddScoped<AccessAdministration>();
        services.AddSingleton<OutboxProcessor>();
        services.AddHealthChecks().AddDbContextCheck<PlatformDbContext>("platform-db", tags: ["ready"]);
        return services;
    }
}
