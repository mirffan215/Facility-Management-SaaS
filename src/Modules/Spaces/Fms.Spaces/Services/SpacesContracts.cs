using Fms.Spaces.Domain;

namespace Fms.Spaces.Services;

public sealed record SpaceDto(Guid LocationId, string Code, string Name, SpaceKind? Kind, int Capacity, string? Notes, int AllocatedToday, string? RowVersion);

public sealed record SaveSpaceRequest(SpaceKind? Kind, int? Capacity, string? Notes, string? RowVersion);

public sealed record PlanDto(Guid Id, Guid FloorId, string FileName, string ContentType, long SizeBytes, DateTimeOffset UploadedAt);

public sealed record PinDto(Guid Id, PinKind Kind, Guid TargetId, string Code, string Name, double X, double Y);

public sealed record PinInput(PinKind? Kind, Guid? TargetId, double? X, double? Y);

public sealed record SavePinsRequest(IReadOnlyList<PinInput>? Pins);

/// <summary>Everything the plan screen needs for one floor: the image, the pins and the spaces that can be pinned.</summary>
public sealed record FloorViewDto(Guid FloorId, string FloorCode, string FloorName, PlanDto? Plan, IReadOnlyList<PinDto> Pins, IReadOnlyList<SpaceDto> Spaces);

public sealed record DownloadedPlan(PlanDto Plan, Stream Content);

public enum AllocationState { Upcoming = 1, Current = 2, Ended = 3, Cancelled = 4 }

public sealed record AllocationDto(
    Guid Id, Guid SpaceId, string SpaceCode, string SpaceName, OccupantKind Kind, Guid? UserId, string? UserName, string? TeamName, int Headcount, DateOnly StartOn, DateOnly? EndOn,
    AllocationState State, string? Note, string? EndReason, string RowVersion);

public sealed record CreateAllocationRequest(Guid? SpaceId, OccupantKind? Kind, Guid? UserId, string? TeamName, int? Headcount, DateOnly? StartOn, DateOnly? EndOn, string? Note);

public sealed record ReleaseAllocationRequest(string? Reason, DateOnly? From, string? RowVersion);

public sealed record OccupancyRow(Guid SpaceId, string Code, string Name, SpaceKind? Kind, int Capacity, int Allocated, decimal? UtilisationPercent);

public sealed record OccupancyDto(Guid LocationId, DateOnly On, int Capacity, int Allocated, decimal? UtilisationPercent, int SpacesWithoutCapacity, IReadOnlyList<OccupancyRow> Rows);
