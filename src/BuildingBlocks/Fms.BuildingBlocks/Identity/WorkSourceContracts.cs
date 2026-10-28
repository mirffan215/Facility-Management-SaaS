namespace Fms.BuildingBlocks.Identity;

/// <summary>
/// A work order wanted because something else happened: an energy alarm, a failed inspection. <paramref name="Kind"/> and <paramref name="SourceId"/> together name the cause;
/// asking twice for the same cause gives the same work order. Give a place, an asset (whose place is used), or both.
/// </summary>
public sealed record WorkFromSource(string Kind, Guid SourceId, string Title, string? Description, Guid? LocationId, Guid? AssetId, string Priority, bool Emergency);

public sealed record WorkFromSourceResult(Guid WorkOrderId, string Number, bool Created);

/// <summary>What other modules may ask the Work module when they want work raised. Answered for the current tenant only.</summary>
public interface IWorkOrderFromSource
{
    /// <summary>
    /// Raises the work order for this cause, or returns the one already raised. Null when neither the place nor the asset can be found.
    /// <paramref name="requestedBy"/> is the person it is raised in the name of (the person who did the thing, or who set up the rule).
    /// </summary>
    Task<WorkFromSourceResult?> EnsureAsync(WorkFromSource request, Guid requestedBy, CancellationToken ct = default);

    /// <summary>The work orders already raised for these causes of one kind, by source id.</summary>
    Task<IReadOnlyDictionary<Guid, WorkFromSourceResult>> FindAsync(string kind, IEnumerable<Guid> sourceIds, CancellationToken ct = default);
}
