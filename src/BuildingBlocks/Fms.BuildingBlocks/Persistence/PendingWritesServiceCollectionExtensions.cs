using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fms.BuildingBlocks.Persistence;

public static class PendingWritesServiceCollectionExtensions
{
    /// <summary>The audit port and outbox, which buffer into <see cref="PendingWrites"/>. Safe to call from every module.</summary>
    public static IServiceCollection AddFmsPendingWrites(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<PendingWrites>();
        services.TryAddScoped<IAuditPort, AuditPort>();
        services.TryAddScoped<IOutbox, Outbox>();
        return services;
    }
}
