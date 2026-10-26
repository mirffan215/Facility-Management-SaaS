namespace Fms.BuildingBlocks.Security;

/// <summary>The permissions of the signed-in user, for modules that make finer decisions than an endpoint policy can (e.g. "own requests only").</summary>
public interface ICurrentPermissions
{
    Task<IReadOnlySet<string>> GetAsync(CancellationToken ct = default);
}
