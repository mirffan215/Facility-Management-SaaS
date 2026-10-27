using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

/// <summary>
/// A secret address that lets a calendar app subscribe to one person's bookings (one way: the app reads, nothing is written back). The address is shown once and only its
/// hash is kept; making a new one stops the old one working. The feed lists only that person's own bookings.
/// </summary>
public sealed class CalendarFeedService(BookingsDbContext db, TenantContext tenant, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public async Task<CalendarFeedStatusDto> StatusAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return new CalendarFeedStatusDto(false, null, null);
        var t = await db.FeedTokens.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me, ct);
        return new CalendarFeedStatusDto(t is not null, t?.CreatedAt, t?.LastUsedAt);
    }

    public async Task<Result<CalendarFeedDto>> CreateAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<CalendarFeedDto>.Forbidden("Sign in first.");
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var existing = await db.FeedTokens.FirstOrDefaultAsync(x => x.UserId == me, ct);
        if (existing is null) db.FeedTokens.Add(new CalendarFeedToken { UserId = me, TokenHash = Hash(token), CreatedAt = now });
        else
        {
            existing.TokenHash = Hash(token);
            existing.CreatedAt = now;
            existing.LastUsedAt = null;
        }

        audit.Record("calendar.feed_created", nameof(CalendarFeedToken), me.ToString());
        await db.SaveChangesAsync(ct);
        return Result<CalendarFeedDto>.Ok(new CalendarFeedDto($"/api/v1/calendar/{token}", now));
    }

    public async Task<Result<CalendarFeedStatusDto>> RevokeAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<CalendarFeedStatusDto>.Forbidden("Sign in first.");
        var existing = await db.FeedTokens.FirstOrDefaultAsync(x => x.UserId == me, ct);
        if (existing is not null)
        {
            db.FeedTokens.Remove(existing);
            audit.Record("calendar.feed_revoked", nameof(CalendarFeedToken), me.ToString());
            await db.SaveChangesAsync(ct);
        }

        return Result<CalendarFeedStatusDto>.Ok(new CalendarFeedStatusDto(false, null, null));
    }

    /// <summary>
    /// The feed for an address, or null when it is not valid (always the same answer, so addresses cannot be probed). The caller has no tenant yet (a calendar app is
    /// anonymous), so this looks the address up across organisations and then reads only that one person's rows, filtered explicitly.
    /// </summary>
    public async Task<string?> RenderAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 100) return null;
        using var platform = tenant.BeginPlatformScope();
        var hash = Hash(token);
        var feed = await db.FeedTokens.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (feed is null) return null;

        var now = clock.GetUtcNow();
        var since = now.AddDays(-30);
        var bookings = await db.Bookings.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == feed.TenantId && b.OrganizerId == feed.UserId && b.EndsAt >= since && BookingRules.Holding.Contains(b.Status))
            .OrderBy(b => b.StartsAt).Take(1000).ToListAsync(ct);
        var ids = bookings.Select(b => b.ResourceId).Distinct().ToList();
        var resources = await db.Resources.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == feed.TenantId && ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        feed.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        return IcsWriter.Build("Bookings", bookings.Select(b =>
        {
            var r = resources.GetValueOrDefault(b.ResourceId);
            return new IcsEvent($"{b.Id}@fms", b.Title, r?.Name, b.Status == BookingStatus.Pending ? "Waiting for approval" : null, b.StartsAt, b.EndsAt, b.Status == BookingStatus.Pending, now);
        }));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
