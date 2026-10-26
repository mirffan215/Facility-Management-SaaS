using Fms.BuildingBlocks.Results;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

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

    public static bool IsQuantity(decimal q) => q > 0 && q <= 1_000_000_000m && decimal.Round(q, 4) == q;

    public static bool IsPrice(decimal p) => p >= 0 && p <= 1_000_000_000m && decimal.Round(p, 4) == p;

    /// <summary>Hands out the next number for a tenant: an application lock held for the caller's transaction, with a unique index as the backstop. Call inside a transaction.</summary>
    public static async Task AcquireNumberLockAsync(PurchasingDbContext db, string what, string tenantKey, CancellationToken ct)
    {
        var r = await db.Database
            .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.purchasing-" + what + ":" + tenantKey}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
            .ToListAsync(ct);
        if (r.Count != 1 || r[0] < 0) throw new InvalidOperationException("Could not reserve a number.");
    }
}
