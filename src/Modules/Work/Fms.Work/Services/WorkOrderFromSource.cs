using Fms.BuildingBlocks.Identity;

namespace Fms.Work.Services;

/// <summary>The Work module's answer to other modules that want work raised for a cause (see <see cref="IWorkOrderFromSource"/>).</summary>
public sealed class WorkOrderFromSource(WorkOrderService orders) : IWorkOrderFromSource
{
    public Task<WorkFromSourceResult?> EnsureAsync(WorkFromSource request, Guid requestedBy, CancellationToken ct = default) => orders.CreateFromSourceAsync(request, requestedBy, ct);

    public Task<IReadOnlyDictionary<Guid, WorkFromSourceResult>> FindAsync(string kind, IEnumerable<Guid> sourceIds, CancellationToken ct = default) => orders.FindFromSourceAsync(kind, sourceIds, ct);
}
