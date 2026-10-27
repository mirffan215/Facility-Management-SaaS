using Fms.BuildingBlocks.Results;
using Fms.Spaces.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Services;

internal static class Common
{
    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }

    public static string Version(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>"Today" in whole days. Allocations are dated in days; the server's UTC date is used for all tenants.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>Serialises changes to one space's allocations so two people cannot both squeeze into the last seat. Held until the caller's transaction ends.</summary>
    public static async Task LockSpaceAsync(SpacesDbContext db, Guid spaceId, CancellationToken ct)
    {
        var r = await db.Database
            .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.spaces-alloc:" + spaceId.ToString("N")}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
            .ToListAsync(ct);
        if (r.Count != 1 || r[0] < 0) throw new InvalidOperationException("Could not lock the space.");
    }
}
