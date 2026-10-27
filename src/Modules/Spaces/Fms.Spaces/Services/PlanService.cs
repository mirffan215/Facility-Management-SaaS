using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Spaces.Domain;
using Fms.Spaces.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Services;

/// <summary>A floor's drawing and where spaces and assets sit on it. Only passive image formats are accepted (never SVG or PDF, which can carry script).</summary>
public sealed class PlanService(SpacesDbContext db, IFacilityLookup facilities, SpaceService spaces, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxPins = 500;
    private const string StorageKind = "floor-plans";
    private static readonly HashSet<string> Images = ["image/png", "image/jpeg", "image/webp"];

    public async Task<Result<FloorViewDto>> ViewAsync(Guid floorId, CancellationToken ct = default)
    {
        var tree = await facilities.LocationsUnderAsync(floorId, ct);
        if (tree.Count == 0 || tree[0].Kind != "Floor") return Result<FloorViewDto>.NotFound();
        var floor = tree[0];
        var plan = await CurrentAsync(floorId, ct);
        var pins = await db.Pins.AsNoTracking().Where(p => p.FloorId == floorId).ToListAsync(ct);

        var spaceRefs = tree.Where(l => l.Kind == "Space").ToList();
        var ids = spaceRefs.Select(s => s.Id).ToList();
        var profiles = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.LocationId)).ToDictionaryAsync(p => p.LocationId, ct);
        var allocated = await spaces.AllocatedAsync(ids, Common.Today(clock), ct);
        var spaceDtos = spaceRefs.Select(s => SpaceService.ToDto(s, profiles.GetValueOrDefault(s.Id), allocated.GetValueOrDefault(s.Id))).ToList();

        var assets = await facilities.AssetsAsync(pins.Where(p => p.Kind == PinKind.Asset).Select(p => p.TargetId), ct);
        var pinDtos = pins.Select(p =>
        {
            var named = p.Kind == PinKind.Space ? spaceRefs.FirstOrDefault(s => s.Id == p.TargetId) : assets.GetValueOrDefault(p.TargetId);
            return new PinDto(p.Id, p.Kind, p.TargetId, named?.Code ?? "?", named?.Name ?? "?", p.X, p.Y);
        }).OrderBy(p => p.Code).ToList();

        return Result<FloorViewDto>.Ok(new FloorViewDto(floor.Id, floor.Code, floor.Name, plan is null ? null : ToDto(plan), pinDtos, spaceDtos));
    }

    public async Task<Result<PlanDto>> UploadAsync(Guid floorId, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        var floor = await facilities.GetLocationAsync(floorId, ct);
        if (floor is not { Kind: "Floor" }) return Result<PlanDto>.NotFound();

        var bytes = await ReadLimitedAsync(content, ct);
        if (bytes is null) return new Result<PlanDto>(ResultStatus.TooLarge, Message: $"A plan can be at most {MaxBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<PlanDto>.Invalid("file", "The file is empty.");
        var detected = FileSniffer.Detect(bytes);
        if (detected is null || !Images.Contains(detected.ContentType)) return new Result<PlanDto>(ResultStatus.UnsupportedType, Message: "A floor plan must be a PNG, JPEG or WebP image.");
        if (!(await scanner.ScanAsync(bytes, ct)).IsClean) return Result<PlanDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var plan = new FloorPlan
        {
            Id = id, FloorId = floorId, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), UploadedAt = now, UploadedBy = user.UserId,
        };
        await using (var stream = new MemoryStream(bytes)) plan.StorageKey = await files.PutAsync(StorageKind, id, "content", stream, ct);
        foreach (var old in await db.Plans.Where(p => p.FloorId == floorId && p.SupersededAt == null).ToListAsync(ct)) old.SupersededAt = now;
        db.Plans.Add(plan);
        audit.Record("space.plan_uploaded", nameof(FloorPlan), id.ToString(), JsonSerializer.Serialize(new { floor.Code, plan.FileName, plan.ContentType, plan.SizeBytes, plan.Sha256 }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await files.DeleteAsync(plan.StorageKey, CancellationToken.None);
            throw;
        }

        return Result<PlanDto>.Ok(ToDto(plan));
    }

    public async Task<Result<DownloadedPlan>> DownloadAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null) return Result<DownloadedPlan>.NotFound();
        var stream = await files.GetAsync(plan.StorageKey, ct);
        if (stream is null) return Result<DownloadedPlan>.NotFound("The stored file is missing.");
        return Result<DownloadedPlan>.Ok(new DownloadedPlan(ToDto(plan), stream));
    }

    /// <summary>Replaces the floor's pins with the given list. A space must be on the floor; an asset must be in a place on the floor.</summary>
    public async Task<Result<FloorViewDto>> SavePinsAsync(Guid floorId, SavePinsRequest r, CancellationToken ct = default)
    {
        var tree = await facilities.LocationsUnderAsync(floorId, ct);
        if (tree.Count == 0 || tree[0].Kind != "Floor") return Result<FloorViewDto>.NotFound();
        if (await CurrentAsync(floorId, ct) is null) return Result<FloorViewDto>.Conflict("Upload the floor plan before placing pins on it.");
        if (r.Pins is null) return Result<FloorViewDto>.Invalid("pins", "Pins are required.");
        if (r.Pins.Count > MaxPins) return Result<FloorViewDto>.Invalid("pins", $"A floor can have at most {MaxPins} pins.");

        var onFloor = tree.Select(l => l.Id).ToHashSet();
        var spaceIds = tree.Where(l => l.Kind == "Space").Select(l => l.Id).ToHashSet();
        var assets = await facilities.AssetsAsync(r.Pins.Where(p => p.Kind == PinKind.Asset && p.TargetId is not null).Select(p => p.TargetId!.Value), ct);
        var errors = new ValidationErrors();
        var seen = new HashSet<(PinKind, Guid)>();
        var next = new List<PlanPin>();
        for (var i = 0; i < r.Pins.Count; i++)
        {
            var p = r.Pins[i];
            if (p.Kind is not { } kind || !Enum.IsDefined(kind) || p.TargetId is not { } target) { errors.Add($"pins[{i}]", "Each pin needs a kind and a target."); continue; }
            if (p.X is not { } x || p.Y is not { } y || !SpaceRules.IsFraction(x) || !SpaceRules.IsFraction(y)) { errors.Add($"pins[{i}]", "A pin's position must be inside the plan."); continue; }
            if (!seen.Add((kind, target))) { errors.Add($"pins[{i}]", "The same thing cannot be pinned twice."); continue; }
            var valid = kind == PinKind.Space ? spaceIds.Contains(target) : assets.TryGetValue(target, out var asset) && asset.LocationId is { } at && onFloor.Contains(at);
            if (!valid) { errors.Add($"pins[{i}]", kind == PinKind.Space ? "That space is not on this floor." : "That asset is not on this floor."); continue; }
            next.Add(new PlanPin { FloorId = floorId, Kind = kind, TargetId = target, X = x, Y = y });
        }

        if (errors.Any) return errors.ToResult<FloorViewDto>();

        db.Pins.RemoveRange(await db.Pins.Where(p => p.FloorId == floorId).ToListAsync(ct));
        db.Pins.AddRange(next);
        audit.Record("space.pins_saved", nameof(PlanPin), floorId.ToString(), JsonSerializer.Serialize(new { count = next.Count }));
        await db.SaveChangesAsync(ct);
        return await ViewAsync(floorId, ct);
    }

    private Task<FloorPlan?> CurrentAsync(Guid floorId, CancellationToken ct) =>
        db.Plans.AsNoTracking().Where(p => p.FloorId == floorId && p.SupersededAt == null).OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);

    private static async Task<byte[]?> ReadLimitedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static PlanDto ToDto(FloorPlan p) => new(p.Id, p.FloorId, p.FileName, p.ContentType, p.SizeBytes, p.UploadedAt);
}
