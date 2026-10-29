using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

/// <summary>
/// A request counter that every server in a deployment shares, kept in the database: one row per key and minute. It exists for the routes protected only by a guessable secret,
/// where a per-server limit would let an attacker multiply their guesses by the number of servers. Old rows are swept as a side effect.
/// </summary>
public sealed class SharedRateLimiter(PlatformDbContext db, TimeProvider clock)
{
    public const int Window = 60;

    /// <summary>Counts one request for the key and says whether it is within the limit, and how long until the minute ends.</summary>
    public async Task<(bool Allowed, int RetryAfterSeconds)> HitAsync(string key, int perMinute, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        var hits = (await db.Database.SqlQuery<int>($"""
            MERGE dbo.RateLimitWindows WITH (HOLDLOCK) AS t
            USING (SELECT {key} AS [Key], {start} AS WindowStart) AS s ON t.[Key] = s.[Key] AND t.WindowStart = s.WindowStart
            WHEN MATCHED THEN UPDATE SET Hits = t.Hits + 1
            WHEN NOT MATCHED THEN INSERT ([Key], WindowStart, Hits) VALUES (s.[Key], s.WindowStart, 1)
            OUTPUT inserted.Hits AS Value;
            """).ToListAsync(ct)).Single();
        if (Random.Shared.Next(50) == 0)
        {
            var cutoff = start.AddHours(-1);
            await db.Database.ExecuteSqlAsync($"DELETE TOP (500) FROM dbo.RateLimitWindows WHERE WindowStart < {cutoff}", ct);
        }

        return (hits <= Math.Max(1, perMinute), Window - now.Second);
    }
}
