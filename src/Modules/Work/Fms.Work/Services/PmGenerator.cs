using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Work.Services;

/// <summary>Runs the preventive engine for every tenant that has schedules. Called by the worker every few minutes; each tenant in its own scope.</summary>
public sealed class PmGenerator(IServiceScopeFactory scopes, ILogger<PmGenerator> log)
{
    /// <summary>Returns the number of work orders created.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            tenants = await scope.ServiceProvider.GetRequiredService<WorkDbContext>().PmSchedules.IgnoreQueryFilters().Where(s => !s.IsArchived).Select(s => s.TenantId).Distinct().ToListAsync(ct);
        }

        var created = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                created += (await scope.ServiceProvider.GetRequiredService<PmEngine>().RunAsync(ct)).WorkOrdersCreated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Preventive maintenance generation failed for a tenant; the others continue");
            }
        }

        return created;
    }
}
