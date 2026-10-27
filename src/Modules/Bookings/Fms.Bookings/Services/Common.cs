using Fms.BuildingBlocks.Results;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

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

    /// <summary>Serialises changes to one resource's bookings so two people cannot both take the same slot. Held until the caller's transaction ends.</summary>
    public static async Task LockResourceAsync(BookingsDbContext db, Guid resourceId, CancellationToken ct)
    {
        var r = await db.Database
            .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.bookings-resource:" + resourceId.ToString("N")}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
            .ToListAsync(ct);
        if (r.Count != 1 || r[0] < 0) throw new InvalidOperationException("Could not lock the resource.");
    }
}
