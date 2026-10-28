using Fms.BuildingBlocks.Identity;
using Microsoft.EntityFrameworkCore;
using Fms.Compliance.Persistence;

namespace Fms.Compliance.Services;

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

    /// <summary>"Today" in whole days; the server's UTC date is used for all organisations.</summary>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static string NameOf(DirectoryUser? u) => u?.DisplayName ?? u?.Email ?? "Unknown";

    public static async Task<string> NameOfAsync(IUserDirectory users, Guid? id, CancellationToken ct)
    {
        if (id is not { } uid) return "System";
        var found = await users.GetAsync([uid], ct);
        return found.TryGetValue(uid, out var u) ? NameOf(u) : "Unknown";
    }

    /// <summary>Serialises work on one place so two permits cannot both be issued for it at once. Held until the caller's transaction ends.</summary>
    public static async Task LockAsync(ComplianceDbContext db, string key, CancellationToken ct)
    {
        var r = await db.Database
            .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.compliance-" + key}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
            .ToListAsync(ct);
        if (r.Count != 1 || r[0] < 0) throw new InvalidOperationException("Could not take the lock.");
    }
}
