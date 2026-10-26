namespace Fms.BuildingBlocks.Identity;

public sealed record DirectoryUser(Guid Id, string? DisplayName, string? Email, bool IsActive);

/// <summary>What other modules may ask about people, without reaching into the platform's tables.</summary>
public interface IUserDirectory
{
    Task<IReadOnlyDictionary<Guid, DirectoryUser>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Active people of the current tenant whose roles carry the permission (e.g. who can be assigned work).</summary>
    Task<IReadOnlyList<DirectoryUser>> WithPermissionAsync(string permission, CancellationToken ct = default);
}

public sealed record FacilityRef(Guid Id, string Code, string Name, Guid? LocationId, string? Kind = null);

/// <summary>What other modules may ask the Facilities module (locations and assets are validated and named, never joined across schemas).</summary>
public interface IFacilityLookup
{
    /// <summary>The location, or null when it does not exist (or belongs to another tenant). Archived locations are returned.</summary>
    Task<FacilityRef?> GetLocationAsync(Guid id, CancellationToken ct = default);

    /// <summary>The asset (<see cref="FacilityRef.LocationId"/> is where it is), or null when it does not exist.</summary>
    Task<FacilityRef?> GetAssetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Finds assets to report a problem against, by what a person types or by what a label encodes. Deliberately thin (id, code, name, place):
    /// it is for people who may raise requests but not browse the asset register.
    /// </summary>
    Task<IReadOnlyList<FacilityRef>> FindAssetsAsync(string? search, string? tag, int limit, CancellationToken ct = default);

    /// <summary>The site a location belongs to (the location itself when it is a site), or null when it does not exist.</summary>
    Task<Guid?> SiteOfAsync(Guid locationId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, FacilityRef>> LocationsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, FacilityRef>> AssetsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}
